using System.IO;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Status reasons are stored as <see cref="LocalizedMessage"/> and translated only when displayed,
/// so that they follow a language switch without any re-translation of stored text.
/// </summary>
public sealed class LocalizedReasonTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LocalizedMessage_ShouldCompareByKeyAndArguments()
    {
        var a = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", "A.adf"), ("reason", LocalizedMessage.Create("reason.1g1r_reason_clean")));
        var b = LocalizedMessage.Create("reason.1g1r_discarded", ("reason", LocalizedMessage.Create("reason.1g1r_reason_clean")), ("winner", "A.adf"));
        var c = LocalizedMessage.Create("reason.1g1r_discarded", ("winner", "B.adf"), ("reason", LocalizedMessage.Create("reason.1g1r_reason_clean")));

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(c);
        LocalizedMessage.Create("reason.crack_excluded").Should().Be(new LocalizedMessage("reason.crack_excluded"));
    }

    [Fact]
    public void Format_ShouldTranslateNestedMessagesInTheActiveLanguage()
    {
        var services = new CoreTestServices("fr");
        var message = LocalizedMessage.Create(
            "reason.1g1r_discarded",
            ("winner", "Game (1990)(Pub)(FR).adf"),
            ("reason", LocalizedMessage.Create("reason.1g1r_reason_lang", ("w_lang", "FR"), ("l_lang", LocalizedMessage.Create("desc.other_language")))));

        services.I18n.Format(message).Should().Be("1G1R : Game (1990)(Pub)(FR).adf retenu (langue FR préférée à autre)");
        services.I18n.SetLocale("en");
        services.I18n.Format(message).Should().Be("1G1R: Game (1990)(Pub)(FR).adf retained (FR preferred over other)");
        services.I18n.Format(null).Should().BeEmpty();
    }

    [Fact]
    public async Task SimulationReasons_ShouldFollowALanguageSwitchWithoutBeingRewritten()
    {
        var src = _temp.CreateSubdirectory("src");
        var dst = _temp.CreateSubdirectory("dst");
        string[] files =
        [
            "Game (1990)(Pub)(FR).adf", "Game (1990)(Pub)(DE).adf", "Game (1990)(Pub)(FR)[b].adf",
            "Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf", "readme.txt"
        ];
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var services = new CoreTestServices("fr");
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Filters = new FilterConfig { OneGameOneRom = true, PriorityLanguages = ["fr"], CompleteMultidiskSetsOnly = true }
        };
        var result = await services.Processor.ExecuteAsync(config, isDryRun: true);
        var reasons = result.Items.ToDictionary(it => it.DisplayName, it => it.StatusReason);

        string Reason(string name) => services.I18n.Format(result.Items.Single(it => it.DisplayName == name).StatusReason);

        Reason("Game (1990)(Pub)(DE).adf").Should().Be("1G1R : Game (1990)(Pub)(FR).adf retenu (langue FR préférée à autre)");
        Reason("Game (1990)(Pub)(FR)[b].adf").Should().Be("Bad Dump [b] exclu");
        Reason("Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf").Should().Be("Set multi-disquettes incomplet (1/2 disques trouvés)");
        Reason("readme.txt").Should().Be("Nom non conforme au format TNC minimal");

        services.I18n.SetLocale("en");

        Reason("Game (1990)(Pub)(DE).adf").Should().Be("1G1R: Game (1990)(Pub)(FR).adf retained (FR preferred over other)");
        Reason("Game (1990)(Pub)(FR)[b].adf").Should().Be("Bad Dump [b] excluded");
        Reason("Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf").Should().Be("Incomplete multi-disk set (1/2 disks found)");
        Reason("readme.txt").Should().Be("Filename does not conform to minimal TNC format");

        // The stored reasons are untouched: only their display changes
        result.Items.Should().OnlyContain(it => Equals(reasons[it.DisplayName], it.StatusReason));
    }

    [Fact]
    public async Task TransferLogs_ShouldFollowALanguageSwitch()
    {
        var src = _temp.CreateSubdirectory("src_logs");
        var dst = _temp.CreateSubdirectory("dst_logs");
        string[] files =
        [
            "Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf", "Zak (1988)(Lucas)(FR)(Disk 2 of 2).adf",
            "Game (1990)(Pub)(FR)[b].adf", "readme.txt"
        ];
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(src, name), name);
        }

        var services = new CoreTestServices("fr");
        var logs = new List<LogMessage>();
        var config = new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = "{title}/",
            GenerateM3u = true,
            Filters = new FilterConfig { ExcludeBadDumps = true }
        };
        await services.Processor.ExecuteAsync(config, isDryRun: false, logProgress: new SyncProgress<LogMessage>(logs.Add));

        string[] Render(LogLevel level) =>
            logs.Where(l => l.Level == level).Select(l => services.I18n.Format(l.Message).Replace(Path.DirectorySeparatorChar, '/')).ToArray();

        Render(LogLevel.Ok).Should().Contain(
        [
            "Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf -> Zak/Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf",
            "Playlist .m3u générée : Zak (1988)(Lucas)(FR).m3u"
        ]);
        Render(LogLevel.Skip).Should().Contain("Bad Dump [b] exclu : Game (1990)(Pub)(FR)[b].adf");
        Render(LogLevel.Unmatched).Should().Contain("readme.txt -> _unmatched/readme.txt");

        services.I18n.SetLocale("en");

        Render(LogLevel.Ok).Should().Contain(
        [
            "Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf -> Zak/Zak (1988)(Lucas)(FR)(Disk 1 of 2).adf",
            ".m3u playlist generated: Zak (1988)(Lucas)(FR).m3u"
        ]);
        Render(LogLevel.Skip).Should().Contain("Bad Dump [b] excluded : Game (1990)(Pub)(FR)[b].adf");
        Render(LogLevel.Info).Should().Contain("Starting operation: Sort Execution");
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
