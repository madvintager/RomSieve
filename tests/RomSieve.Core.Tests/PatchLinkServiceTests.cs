using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using static RomSieve.Core.Tests.Fixtures.PatchTestEncoder;

namespace RomSieve.Core.Tests;

/// <summary>
/// The choice of the user for each patch, kept in <c>patch_links.json</c> (<see cref="PatchLinkService"/>): the key of a
/// patch, the file written and read back, a choice made elsewhere kept, a file that cannot be read never lost, a file of
/// a newer format never written, the entries this build cannot read written back, and the forms of an origin to choose.
/// </summary>
public sealed class PatchLinkServiceTests : IDisposable
{
    private static readonly PatchKey Ips = PatchKey.Of(Encoding.ASCII.GetBytes("PATCH-one-EOF"));
    private static readonly PatchKey Bps = PatchKey.Of(Encoding.ASCII.GetBytes("BPS1-two"));

    private readonly CoreTestServices _services = new();
    private readonly TempDirectoryFixture _temp = new();
    private readonly List<(LogLevel Level, LocalizedMessage Message)> _log = [];

    public void Dispose() => _temp.Dispose();

    private string LinksPath => _temp.GetFilePath(PatchLinkService.FileName);

    private PatchLinkService CreateService() => new(LinksPath, _services.ChecksumService, _services.CrcCache);

    private Task<bool> UpdateAsync(PatchLinkService service, params (PatchKey Key, PatchLink? Link)[] changes) =>
        service.UpdateAsync(
            [.. changes.Select(c => new KeyValuePair<PatchKey, PatchLink?>(c.Key, c.Link))],
            (level, message) => _log.Add((level, message)));

    private string[] UnreadableCopies() =>
        Directory.GetFiles(_temp.Path, "patch_links.unreadable-*.json");

    // =========================================================================
    // The key of a patch
    // =========================================================================

    [Fact]
    public void TheKey_ShouldBeTheSha1AndTheSizeOfTheContent()
    {
        var key = PatchKey.Of("abc"u8);

        key.Should().Be(new PatchKey("a9993e364706816aba3e25717850c26c9cd0d89d", 3));
        PatchKey.IsValidSha1(key.Sha1).Should().BeTrue();
        PatchKey.Of("abc"u8).Should().Be(key, "the same content gives the same key, wherever the patch is");
        PatchKey.Of("abd"u8).Should().NotBe(key);
    }

    [Theory]
    [InlineData("A9993E364706816ABA3E25717850C26C9CD0D89D")]
    [InlineData("a9993e364706816aba3e25717850c26c9cd0d89")]
    [InlineData("g9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("")]
    [InlineData(null)]
    public void ASha1_NotWrittenTheWayTheKeyWritesIt_ShouldBeInvalid(string? sha1)
    {
        PatchKey.IsValidSha1(sha1).Should().BeFalse();
    }

    // =========================================================================
    // Writing and reading back
    // =========================================================================

    [Fact]
    public void WithoutFile_ThereShouldBeNoChoice_AndNothingToSay()
    {
        var snapshot = CreateService().Load();

        snapshot.Links.Should().BeEmpty();
        snapshot.Warning.Should().BeNull();
        File.Exists(LinksPath).Should().BeFalse("reading writes nothing");
    }

    [Fact]
    public async Task TheChoices_ShouldBeReadBack_ByAnotherInstance()
    {
        var service = CreateService();

        (await UpdateAsync(
            service,
            (Ips, PatchLink.ToOrigin("Pokémon (T-Fr).ips", 0x47e70e08, "Pokémon (Japan).gb")),
            (Bps, PatchLink.Ignore("Game (T-En).bps")))).Should().BeTrue();

        var snapshot = CreateService().Load();
        snapshot.Warning.Should().BeNull();
        snapshot.Links.Should().BeEquivalentTo(new Dictionary<PatchKey, PatchLink>
        {
            [Ips] = PatchLink.ToOrigin("Pokémon (T-Fr).ips", 0x47e70e08, "Pokémon (Japan).gb"),
            [Bps] = PatchLink.Ignore("Game (T-En).bps")
        });
        _log.Should().BeEmpty();
    }

    [Fact]
    public async Task TheFile_ShouldBeReadable_WithItsVersionAndTheNamesAsTheyAre()
    {
        await UpdateAsync(CreateService(), (Ips, PatchLink.ToOrigin("Pokémon (T-Fr).ips", 0x0000abcd, "Pokémon (Japan).gb")));

        var text = File.ReadAllText(LinksPath);
        text.Should().Contain("\"version\": 1")
            .And.Contain("\"patch_name\": \"Pokémon (T-Fr).ips\"")
            .And.Contain("\"kind\": \"origin\"")
            .And.Contain("\"rom_crc32\": \"0000abcd\"")
            .And.Contain($"\"patch_sha1\": \"{Ips.Sha1}\"");
        File.ReadAllBytes(LinksPath).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte order mark");
    }

    [Fact]
    public async Task BackToTheAutomaticChoice_ShouldRemoveTheChoice()
    {
        var service = CreateService();
        await UpdateAsync(service, (Ips, PatchLink.Ignore("a.ips")), (Bps, PatchLink.Ignore("b.bps")));

        (await UpdateAsync(service, (Ips, null))).Should().BeTrue();

        service.Load().Links.Keys.Should().Equal(Bps);
    }

    [Fact]
    public async Task AChoice_ShouldReplaceTheOneBefore()
    {
        var service = CreateService();
        await UpdateAsync(service, (Ips, PatchLink.ToOrigin("a.ips", 1, "One.sfc")));

        await UpdateAsync(service, (Ips, PatchLink.ToOrigin("a.ips", 2, "Two.sfc")));

        service.Load().Links[Ips].Should().Be(PatchLink.ToOrigin("a.ips", 2, "Two.sfc"));
    }

    [Fact]
    public async Task AChoiceMadeElsewhere_Meanwhile_ShouldBeKept()
    {
        var interfaceSide = CreateService();
        var commandLine = CreateService();
        interfaceSide.Load();

        await UpdateAsync(commandLine, (Bps, PatchLink.Ignore("b.bps")));
        await UpdateAsync(interfaceSide, (Ips, PatchLink.Ignore("a.ips")));

        CreateService().Load().Links.Keys.Should().BeEquivalentTo([Ips, Bps]);
    }

    [Fact]
    public async Task AnOriginWithoutCrc_ShouldBeRefused()
    {
        var update = () => UpdateAsync(CreateService(), (Ips, new PatchLink(PatchLinkKind.Origin, "a.ips")));

        await update.Should().ThrowAsync<ArgumentException>();
        File.Exists(LinksPath).Should().BeFalse();
    }

    [Fact]
    public void AFileWithoutVersion_WithAByteOrderMark_ShouldBeRead()
    {
        _temp.WriteText(PatchLinkService.FileName, $$"""
            { "links": [ { "patch_sha1": "{{Ips.Sha1}}", "patch_size": {{Ips.Size}}, "kind": "ignored" } ] }
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var snapshot = CreateService().Load();

        snapshot.Warning.Should().BeNull();
        snapshot.Links.Should().ContainKey(Ips).WhoseValue.Should().Be(PatchLink.Ignore(string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void AnEmptyFile_ShouldHoldNoChoice_WithoutBeingSetAside(string content)
    {
        _temp.WriteText(PatchLinkService.FileName, content);

        var snapshot = CreateService().Load();

        snapshot.Links.Should().BeEmpty();
        snapshot.Warning.Should().BeNull();
        UnreadableCopies().Should().BeEmpty();
    }

    [Fact]
    public async Task TheEntriesThisBuildCannotRead_ShouldBeWrittenBack()
    {
        _temp.WriteText(PatchLinkService.FileName, $$"""
            { "version": 1, "links": [
              { "patch_sha1": "{{Bps.Sha1}}", "patch_size": {{Bps.Size}}, "patch_name": "b.bps", "kind": "ignored" },
              { "patch_sha1": "{{Ips.Sha1}}", "patch_size": {{Ips.Size}}, "kind": "later", "extra": [1, 2] },
              { "patch_sha1": "not a sha1", "patch_size": 3, "kind": "ignored" },
              { "patch_sha1": "{{Ips.Sha1}}", "patch_size": "big", "kind": "ignored" },
              { "patch_sha1": "{{Ips.Sha1}}", "patch_size": {{Ips.Size}}, "kind": "origin", "rom_crc32": "xyz" }
            ] }
            """);
        var service = CreateService();
        service.Load().Links.Keys.Should().Equal(Bps);

        await UpdateAsync(service, (Bps, null));

        var text = File.ReadAllText(LinksPath);
        text.Should().Contain("\"kind\": \"later\"").And.Contain("\"extra\"").And.Contain("not a sha1").And.Contain("\"xyz\"");
        text.Should().NotContain("b.bps");
    }

    // =========================================================================
    // A file that cannot be read, or of a newer format
    // =========================================================================

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"version\": \"two\" }")]
    [InlineData("{ \"links\": {} }")]
    public void AnUnreadableFile_ShouldBeCopiedAside_ThenEmptied_Once(string content)
    {
        _temp.WriteText(PatchLinkService.FileName, content);
        var service = CreateService();

        var first = service.Load();
        var second = service.Load();

        first.Links.Should().BeEmpty();
        first.Warning!.Key.Should().Be("log.patch_links_unreadable_preserved");
        var copy = (string)first.Warning.Args["copy"]!;
        UnreadableCopies().Should().Equal(copy);
        File.ReadAllText(copy).Should().Be(content, "the copy is the file as it was");
        second.Warning.Should().BeNull("the file was emptied: it is copied once");
    }

    [Fact]
    public async Task AnUnreadableFile_ShouldBeSetAside_BeforeAChoiceIsWritten()
    {
        _temp.WriteText(PatchLinkService.FileName, "{ not json");

        (await UpdateAsync(CreateService(), (Ips, PatchLink.Ignore("a.ips")))).Should().BeTrue();

        _log.Should().ContainSingle().Which.Should().Match<(LogLevel Level, LocalizedMessage Message)>(
            l => l.Level == LogLevel.Warn && l.Message.Key == "log.patch_links_unreadable_preserved");
        UnreadableCopies().Should().ContainSingle();
        CreateService().Load().Links.Keys.Should().Equal(Ips);
    }

    [Fact]
    public async Task AnUnreadableFile_ThatCannotBeCopied_ShouldBeLeftAsItIs_ForTheSession()
    {
        const string content = "{ not json";
        _temp.WriteText(PatchLinkService.FileName, content);

        // Every name the copy could take in the next seconds is already taken: a copy never replaces an earlier one
        var now = DateTime.Now;
        for (var second = -1; second <= 30; second++)
        {
            var stamp = now.AddSeconds(second).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _temp.WriteText($"patch_links.unreadable-{stamp}.json", "earlier copy");
        }

        var service = CreateService();

        var snapshot = service.Load();
        var saved = await UpdateAsync(service, (Ips, PatchLink.Ignore("a.ips")));

        snapshot.Links.Should().BeEmpty();
        snapshot.Warning!.Key.Should().Be("log.patch_links_unreadable_kept");
        saved.Should().BeFalse();
        _log.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
        File.ReadAllText(LinksPath).Should().Be(content);
        service.Load().Warning!.Key.Should().Be("log.patch_links_unreadable_kept", "it is not copied at each read");
    }

    [Fact]
    public async Task AFileOfANewerFormat_ShouldBeNeitherReadNorWritten()
    {
        var content = $$"""
            { "version": 2, "links": [ { "patch_sha1": "{{Ips.Sha1}}", "patch_size": {{Ips.Size}}, "kind": "ignored" } ] }
            """;
        _temp.WriteText(PatchLinkService.FileName, content);
        var service = CreateService();

        var snapshot = service.Load();
        var saved = await UpdateAsync(service, (Bps, PatchLink.Ignore("b.bps")));

        snapshot.Links.Should().BeEmpty();
        snapshot.Warning!.Key.Should().Be("log.patch_links_newer");
        saved.Should().BeFalse();
        _log.Should().ContainSingle().Which.Message.Key.Should().Be("log.patch_links_newer");
        File.ReadAllText(LinksPath).Should().Be(content);
        UnreadableCopies().Should().BeEmpty();
    }

    [Fact]
    public async Task ALockedFile_ShouldBeNeitherReadNorSetAside_NorWritten()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows enforces an exclusive opening
            return;
        }

        var service = CreateService();
        await UpdateAsync(service, (Ips, PatchLink.Ignore("a.ips")));
        var content = File.ReadAllText(LinksPath);

        PatchLinkSnapshot snapshot;
        bool saved;
        using (new FileStream(LinksPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            snapshot = service.Load();
            saved = await UpdateAsync(service, (Bps, PatchLink.Ignore("b.bps")));
        }

        snapshot.Warning!.Key.Should().Be("log.patch_links_read_failed");
        saved.Should().BeFalse();
        UnreadableCopies().Should().BeEmpty();
        File.ReadAllText(LinksPath).Should().Be(content);
        service.Load().Links.Keys.Should().Equal(Ips);
    }

    // =========================================================================
    // Forms of an origin
    // =========================================================================

    private static byte[] Data(int seed, int size)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFormsOfAnOrigin_ShouldBeBothForAFileWithACopierHeader_TheWholeOneFirst(bool detectCopierHeaders)
    {
        var japan = Data(1, 4096);
        var data = Data(3, 8192);
        var file = (byte[])[.. Ascii("NES\u001a"), .. new byte[12], .. data];
        _temp.WriteBytes("src/Game (USA).nes", file);
        _temp.WriteBytes("src/Plain (USA).nes", japan);
        var dat = _temp.WriteText("dat/nes.dat", $"""
            <?xml version="1.0"?><datafile><header><name>Nintendo - NES</name><homepage>No-Intro</homepage></header>
            <game name="Game (USA)"><description>Game (USA)</description><rom name="Game (USA).nes" size="{data.Length}" crc="{Crc(data):x8}"/></game>
            <game name="Plain (USA)"><description>Plain (USA)</description><rom name="Plain (USA).nes" size="{japan.Length}" crc="{Crc(japan):x8}"/></game></datafile>
            """);
        var result = await _services.Processor.ExecuteAsync(
            new SortConfig { SourceDir = _temp.GetFilePath("src"), DestinationDir = _temp.CreateSubdirectory("dst"), DatFile = dat, NamingConvention = NamingConvention.NoIntro },
            isDryRun: true);
        var game = result.Items.Single(it => it.DisplayName == "Game (USA).nes");
        var plain = result.Items.Single(it => it.DisplayName == "Plain (USA).nes");
        var service = CreateService();

        var forms = await service.GetOriginFormsAsync(game, detectCopierHeaders);
        var plainForms = await service.GetOriginFormsAsync(plain, detectCopierHeaders);

        if (detectCopierHeaders)
        {
            forms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal(
                (Crc(file), 0L, (bool?)true),
                (Crc(data), 16L, (bool?)false));
        }
        else
        {
            forms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal((Crc(data), 16L, (bool?)null));
        }

        plainForms.Select(f => (f.Crc32, f.Offset, f.WithCopierHeader)).Should().Equal((Crc(japan), 0L, (bool?)null));
        forms.Should().OnlyContain(f => ReferenceEquals(f.Origin, game));
    }

    // =========================================================================
    // Wiring
    // =========================================================================

    [Fact]
    public void TheService_ShouldBeRegistered_NextToTheDefaultConfiguration()
    {
        using var provider = new ServiceCollection().AddRomSieveCore().BuildServiceProvider();

        var service = provider.GetRequiredService<IPatchLinkService>();

        service.Should().BeOfType<PatchLinkService>();
        service.FilePath.Should().Be(PatchLinkService.PathNextTo(provider.GetRequiredService<IConfigurationService>().GetDefaultConfigPath()));
    }

    [Fact]
    public void TheFile_ShouldBeNextToTheConfigurationFileInUse()
    {
        var config = _temp.GetFilePath(Path.Combine("isolated", "config.json"));

        PatchLinkService.PathNextTo(config).Should().Be(_temp.GetFilePath(Path.Combine("isolated", "patch_links.json")));
    }
}
