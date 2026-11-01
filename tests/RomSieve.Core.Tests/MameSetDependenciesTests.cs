using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Sets a MAME set needs (<see cref="MameSetDependencies"/>): every set that needs a required set is named, not only the
/// first found, and the messages list a few of them before counting the others.
/// </summary>
public sealed class MameSetDependenciesTests
{
    private static ProcessItem Set(string name, params string[] requiredSets) => new()
    {
        SourcePath = name + ".zip",
        FileSize = 1,
        Parsed = new ParsedRomName { RawFilename = name + ".zip", Title = name },
        MameSet = new MameSetInfo(name, 0, 0, 0) { RequiredSets = requiredSets }
    };

    private static string Translate(LocalizedMessage message, string locale = "en")
    {
        var i18n = new LocalizationService("fr");
        i18n.SetLocale(locale);
        return i18n.Format(message);
    }

    [Fact]
    public void FindRequiredSets_ShouldNameEverySetThatNeedsARequiredSet()
    {
        var bios = Set("neogeo", "ng_memcard");
        var device = Set("ng_memcard");
        var mslug = Set("mslug", "neogeo");
        var lresort = Set("lresort", "neogeo");
        var tetris = Set("atetris");

        var required = MameSetDependencies.FindRequiredSets([bios, device, mslug, lresort, tetris], [mslug, lresort, tetris]);

        required.Select(r => (r.Item, string.Join(",", r.RequiredBy))).Should().Equal(
            (bios, "lresort,mslug"),
            (device, "neogeo"));
    }

    [Fact]
    public void FindRequiredSets_ShouldLeaveOutTheRoots()
    {
        var bios = Set("neogeo");
        var mslug = Set("mslug", "neogeo");

        MameSetDependencies.FindRequiredSets([bios, mslug], [bios, mslug]).Should().BeEmpty();
    }

    [Fact]
    public void RequiredSetReason_ShouldNameOneSetOrListTheFirstOnes()
    {
        Translate(MameSetDependencies.DescribeRequiredSetReason(["lresort"]))
            .Should().Be("Kept: the set lresort needs it to run");
        Translate(MameSetDependencies.DescribeRequiredSetReason(["2020bb", "3countb", "aof"]))
            .Should().Be("Kept: the sets 2020bb, 3countb, aof need it to run");
        Translate(MameSetDependencies.DescribeRequiredSetReason(["2020bb", "3countb", "aof", "kof98", "mslug"]))
            .Should().Be("Kept: the sets 2020bb, 3countb, aof and 2 others need it to run");
        Translate(MameSetDependencies.DescribeRequiredSetReason(["2020bb", "3countb", "aof", "kof98", "mslug"]), "fr")
            .Should().Be("Gardé : les sets 2020bb, 3countb, aof et 2 autres en ont besoin pour fonctionner");

        MameSetDependencies.IsRequiredSetReason(MameSetDependencies.DescribeRequiredSetReason(["lresort"])).Should().BeTrue();
        MameSetDependencies.IsRequiredSetReason(MameSetDependencies.DescribeRequiredSetReason(["lresort", "mslug"])).Should().BeTrue();
        MameSetDependencies.IsRequiredSetReason(LocalizedMessage.Create("reason.1g1r_winner")).Should().BeFalse();
    }

    private static readonly DatCatalog Fixture = new RomSieve.Core.Tests.Fixtures.CoreTestServices().DatService
        .ParseDatFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mame", "listxml.dat"))
        .GetAwaiter().GetResult();

    private static string[] Dependents(DatCatalog catalog, string set) =>
        catalog.GetMameDependents(set).Select(g => g.Name).ToArray();

    [Fact]
    public void GetMameDependents_ShouldListTheMachinesThatTakeRomsFromASet()
    {
        // The games of a BIOS, the clones of a parent, the machines that use a device, in DAT order
        Dependents(Fixture, "neogeo").Should().Equal("mslug");
        Dependents(Fixture, "sf2").Should().Equal("sf2b", "sf2en", "sf2j", "sf2ua", "sf2ug", "sf2uh");
        Dependents(Fixture, "hd44780").Should().Equal("3lilpigs");

        // A BIOS absent from the DAT still has its games, and their clones through them
        Dependents(Fixture, "sys256").Should().Equal("timecrs4", "timecrs4j");
        Dependents(Fixture, "atetris").Should().BeEmpty();
        Dependents(Fixture, "sf2ua").Should().BeEmpty();
    }

    [Fact]
    public void GetMameDependents_ShouldFollowADeviceMergedIntoAnotherOne()
    {
        // As qsound_hle, whose only ROM is merged into qsound, in MAME 0.289: the CPS2 games use qsound through it
        var catalog = new DatCatalog();
        catalog.AddGame(new DatGame { Name = "qsound", IsDevice = true });
        catalog.AddGame(new DatGame { Name = "qsound_hle", IsDevice = true, RomOf = "qsound" });
        catalog.AddGame(new DatGame { Name = "19xx", DeviceRefs = ["qsound_hle", "z80"] });
        catalog.AddGame(new DatGame { Name = "19xxj", CloneOf = "19xx", RomOf = "19xx", DeviceRefs = ["qsound_hle", "z80"] });
        catalog.AddGame(new DatGame { Name = "vgmplay", DeviceRefs = ["qsound", "vgmplay"] });

        Dependents(catalog, "qsound").Should().Equal("19xx", "19xxj", "vgmplay");
        Dependents(catalog, "qsound_hle").Should().Equal("19xx", "19xxj");
        Dependents(catalog, "vgmplay").Should().BeEmpty("a machine that lists itself does not use itself");
    }
}
