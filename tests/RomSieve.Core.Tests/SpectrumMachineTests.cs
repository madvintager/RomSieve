using System.IO;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// ZX Spectrum the collection is sorted for (<see cref="FilterConfig.SpectrumTarget"/>, <see cref="SpectrumMachines"/>):
/// the machine of a release is read from the system field of its TOSEC name, the combined marks (<c>(48K-128K)</c>) too;
/// with a chosen model, the releases it cannot run are excluded and the machine versions of a game are one game, the
/// 1G1R keeping the one that makes the most of the model; a Timex release stays a game of its own.
/// </summary>
public sealed class SpectrumMachineTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("fr");

    public void Dispose() => _temp.Dispose();

    private async Task<IReadOnlyList<ProcessItem>> SimulateAsync(SpectrumTarget target, params string[] files)
    {
        var src = _temp.CreateSubdirectory("src_" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var result = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = src,
            DestinationDir = _temp.CreateSubdirectory("dst_" + Guid.NewGuid().ToString("N")[..8]),
            Pattern = "{title}/",
            Filters = new FilterConfig
            {
                OneGameOneRom = true,
                PriorityLanguages = ["en"],
                CompleteMultidiskSetsOnly = true,
                SmartMultidiskGrouping = true,
                SpectrumTarget = target
            }
        }, isDryRun: true);
        return result.Items.ToList();
    }

    private static string[] Kept(IEnumerable<ProcessItem> items) =>
        items.Where(it => it.Status == FileStatus.Valid).Select(it => it.DisplayName).Order(StringComparer.Ordinal).ToArray();

    private static ProcessItem Named(IEnumerable<ProcessItem> items, string name) => items.Single(it => it.DisplayName == name);

    [Theory]
    [InlineData("Arc of Yesod, The (1985)(Thor Computer)(48K-128K).tzx", "48K-128K", SpectrumMachine.K48K128)]
    [InlineData("Maze Chase (1983)(Hewson Consultants)(16K-48K).tzx", "16K-48K", SpectrumMachine.K16K48)]
    [InlineData("Game (1988)(Pub)(+2A-+3).tzx", "+2A-+3", SpectrumMachine.Plus3)]
    [InlineData("4D Time-Gate (1983)(Quicksilva)(TC2048-TS2068)(Side A).tzx", "TC2048-TS2068", SpectrumMachine.Timex)]
    [InlineData("Game (1985)(Pub)(Timex Tx2068).tzx", "Timex Tx2068", SpectrumMachine.Timex)]
    [InlineData("Game (1992)(Pub)(Pentagon 128K).tzx", "Pentagon 128K", SpectrumMachine.K128)]
    [InlineData("Arc of Yesod, The (1985)(Thor Computer)(128K).tzx", "128K", SpectrumMachine.K128)]
    [InlineData("Game (1983)(Pub)(16K).tzx", "16K", SpectrumMachine.K16)]
    [InlineData("Arc of Yesod, The (1985)(Thor Computer).tzx", null, SpectrumMachine.Unmarked)]
    public void TosecParser_ShouldReadTheMachineOfASpectrumRelease(string name, string? system, SpectrumMachine machine)
    {
        // The combined marks were lost: a 48K-128K version was taken for another dump of the 48K one
        var parsed = _services.TosecParser.ParseTosecFilename(name);

        parsed.System.Should().Be(system);
        SpectrumMachines.Of(parsed).Should().Be(machine);
    }

    [Theory]
    [InlineData(SpectrumMachine.K128, SpectrumTarget.Zx48, false)]
    [InlineData(SpectrumMachine.Plus3, SpectrumTarget.Zx48, false)]
    [InlineData(SpectrumMachine.K48K128, SpectrumTarget.Zx48, true)]
    [InlineData(SpectrumMachine.Plus3, SpectrumTarget.Zx128, false)]
    [InlineData(SpectrumMachine.K128, SpectrumTarget.Zx128, true)]
    [InlineData(SpectrumMachine.Plus3, SpectrumTarget.ZxPlus3, true)]
    [InlineData(SpectrumMachine.Timex, SpectrumTarget.Zx48, true)]
    [InlineData(SpectrumMachine.Unmarked, SpectrumTarget.Zx48, true)]
    [InlineData(SpectrumMachine.Plus3, SpectrumTarget.Any, true)]
    public void RunsOn_ShouldOnlyLeaveOutTheReleasesOfALaterModel(SpectrumMachine machine, SpectrumTarget target, bool runs)
    {
        SpectrumMachines.RunsOn(machine, target).Should().Be(runs);
    }

    [Fact]
    public async Task OnA128K_ShouldKeepThe128KVersionOfAGame()
    {
        // The 128K version was a game of its own, written next to the 48K one; the 48K-128K one a duplicate of the 48K one
        var items = await SimulateAsync(
            SpectrumTarget.Zx128,
            "Arc of Yesod, The (1985)(Thor Computer).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(48K-128K).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(128K).tzx");

        Kept(items).Should().Equal("Arc of Yesod, The (1985)(Thor Computer)(128K).tzx");
        var discarded = Named(items, "Arc of Yesod, The (1985)(Thor Computer)(48K-128K).tzx");
        discarded.Status.Should().Be(FileStatus.Excluded);
        discarded.EliminatedBy.Should().Be(TiebreakCriterion.SpectrumMachine);
        _services.I18n.Format(discarded.EliminationDetail!).Should().Be(
            "version 128K préférée à la version 48K / 128K (ZX Spectrum 128K, +2)");
    }

    [Fact]
    public async Task OnA48K_ShouldExcludeThe128KVersionAndKeepThe48KOne()
    {
        var items = await SimulateAsync(
            SpectrumTarget.Zx48,
            "Arc of Yesod, The (1985)(Thor Computer).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(48K-128K).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(128K).tzx");

        Kept(items).Should().Equal("Arc of Yesod, The (1985)(Thor Computer).tzx");
        _services.I18n.Format(Named(items, "Arc of Yesod, The (1985)(Thor Computer)(128K).tzx").StatusReason!).Should().Be(
            "Demande un ZX Spectrum 128K : ne tourne pas sur la machine cible (ZX Spectrum 16K, 48K)");
    }

    [Fact]
    public async Task OnEveryMachine_ShouldKeepEachMachineVersionAsAGameOfItsOwn()
    {
        // The 48K-128K version is no longer a duplicate of the 48K one decided by the name
        var items = await SimulateAsync(
            SpectrumTarget.Any,
            "Arc of Yesod, The (1985)(Thor Computer).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(48K-128K).tzx",
            "Arc of Yesod, The (1985)(Thor Computer)(128K).tzx");

        items.Should().OnlyContain(it => it.Status == FileStatus.Valid);
    }

    [Fact]
    public async Task TimexRelease_ShouldNeverReplaceTheSpectrumOne()
    {
        // The two sides of the Timex version won over the Spectrum tape as a "complete set"
        var items = await SimulateAsync(
            SpectrumTarget.Zx128,
            "4D Time-Gate (1982)(Quicksilva).tzx",
            "4D Time-Gate (1983)(Quicksilva)(TC2048-TS2068)(Side A).tzx",
            "4D Time-Gate (1983)(Quicksilva)(TC2048-TS2068)(Side B).tzx");

        Named(items, "4D Time-Gate (1982)(Quicksilva).tzx").Status.Should().Be(FileStatus.Valid);
        items.Should().OnlyContain(it => it.Status == FileStatus.Valid, "the Timex version is a game of its own");
    }

    [Fact]
    public async Task SidesOfTwoMachineVersions_ShouldNeverBeMixed()
    {
        // Side A came from the 48K-128K version and side B from the 48K one
        var items = await SimulateAsync(
            SpectrumTarget.Zx128,
            "Question of Scruples (1987)(Leisure Genius)(48K-128K)(Side A).tzx",
            "Question of Scruples (1987)(Leisure Genius)(48K-128K)(Side B).tzx",
            "Question of Scruples (1987)(Leisure Genius)(Side A).tzx",
            "Question of Scruples (1987)(Leisure Genius)(Side B).tzx");

        Kept(items).Should().Equal(
            "Question of Scruples (1987)(Leisure Genius)(48K-128K)(Side A).tzx",
            "Question of Scruples (1987)(Leisure Genius)(48K-128K)(Side B).tzx");
        items.Should().NotContain(it => it.Status == FileStatus.Duplicate);
    }

    [Fact]
    public void Configuration_ShouldStartOnThe128KAndKeepEveryMachineForAFileOfFormat2()
    {
        new SortSettings().SpectrumTarget.Should().Be(SpectrumTarget.Zx128, "a new configuration");
        new FilterConfig().SpectrumTarget.Should().Be(SpectrumTarget.Any);

        var format2 = new Dictionary<string, object?>(StringComparer.Ordinal) { [ConfigSchema.VersionKey] = 2L };
        ConfigSchema.Migrate(format2)["spectrum_target"].Should().Be("any");
        SortSettingsSerializer.Read(format2).SpectrumTarget
            .Should().Be(SpectrumTarget.Any, "the machine versions of a game stayed apart before format 3");

        var data = new Dictionary<string, object?>();
        SortSettingsSerializer.Write(new SortSettings { SpectrumTarget = SpectrumTarget.ZxPlus3 }, data);
        data["spectrum_target"].Should().Be("plus3");
        SortSettingsSerializer.ToSortConfig(SortSettingsSerializer.Read(data)).Filters.SpectrumTarget.Should().Be(SpectrumTarget.ZxPlus3);
    }
}
