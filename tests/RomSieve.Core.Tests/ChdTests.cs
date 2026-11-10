using System.Buffers.Binary;
using System.IO;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// CHD files: header and track metadata read without decompression, CHD disks of MAME-style DATs identified by the
/// SHA-1 of the header, optional match by name with a DAT that describes the tracks (Redump), damaged CHDs isolated.
/// </summary>
public sealed class ChdTests : IDisposable
{
    private const string RedumpGame = "Shadow of the Beast - Mashou no Okite (Japan) (Rev 1)";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private string WriteChd(string name, byte[] content) => _temp.WriteBytes(Path.Combine("src", name), content);

    private ChdInfo Read(byte[] content) => _services.ChecksumService.ReadChdInfo(_temp.WriteBytes(Guid.NewGuid() + ".chd", content));

    // =========================================================================
    // Header and metadata
    // =========================================================================

    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    public void Header_ShouldGiveTheSha1sAndTheTracks(int version)
    {
        var info = Read(ChdTestFile.Create("game", version, metadata: ChdTestFile.CdTracks(("MODE1_RAW", 13350), ("AUDIO", 23706), ("AUDIO", 19098))));

        info.Version.Should().Be(version);
        info.Sha1.Should().Be(ChdTestFile.Sha1Of("game"));
        info.RawSha1.Should().Be(ChdTestFile.Sha1Of("game:raw"));
        info.LogicalBytes.Should().Be(2_448_000);
        info.ParentSha1.Should().BeNull();
        info.IsDifferential.Should().BeFalse();
        info.MediaKind.Should().Be(ChdMediaKind.Cd);
        info.Tracks.Should().Equal(new ChdTrack(1, "MODE1_RAW", 13350), new ChdTrack(2, "AUDIO", 23706), new ChdTrack(3, "AUDIO", 19098));
    }

    [Fact]
    public void LaserdiscChd_WithLargeVideoMetadata_ShouldBeRead()
    {
        // A laserdisc CHD (cliffhgr, dlair...) holds megabytes of video metadata: they are skipped, never read
        var info = Read(ChdTestFile.Create("cliffhgr", metadata: [("AVAV", Encoding.ASCII.GetBytes("FPS:29.97 WIDTH:720")), ("AVLD", new byte[3 * 1024 * 1024])]));

        info.Sha1.Should().Be(ChdTestFile.Sha1Of("cliffhgr"));
        info.MediaKind.Should().Be(ChdMediaKind.Unknown);
        info.Tracks.Should().BeEmpty();
    }

    [Fact]
    public void MetadataBeyondTheEndOfTheFile_ShouldBeRefused()
    {
        var chd = ChdTestFile.Create("truncated", metadata: [("AVLD", new byte[64 * 1024])]);

        FluentActions.Invoking(() => Read(chd[..^1024])).Should().Throw<InvalidDataException>().WithMessage("Invalid CHD metadata length");
    }

    [Fact]
    public void VersionThreeHeader_ShouldGiveItsSingleSha1()
    {
        var info = Read(ChdTestFile.Create("v3", version: 3));

        info.Sha1.Should().Be(ChdTestFile.Sha1Of("v3"));
        info.RawSha1.Should().Be(info.Sha1);
    }

    [Fact]
    public void DifferentialChd_ShouldGiveItsParent()
    {
        var parent = ChdTestFile.Sha1Of("parent");

        var info = Read(ChdTestFile.Create("child", parentSha1: parent));

        info.ParentSha1.Should().Be(parent);
        info.IsDifferential.Should().BeTrue();
    }

    [Fact]
    public void Metadata_ShouldGiveTheKindOfMedia()
    {
        var gdTrack = ("CHGT", Encoding.ASCII.GetBytes("TRACK:1 TYPE:MODE1 SUBTYPE:NONE FRAMES:300 PAD:0 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0\0"));
        Read(ChdTestFile.Create("gd", metadata: [gdTrack])).MediaKind.Should().Be(ChdMediaKind.GdRom);
        Read(ChdTestFile.Create("dvd", metadata: [("DVD ", [])])).MediaKind.Should().Be(ChdMediaKind.Dvd);
        Read(ChdTestFile.Create("hd", metadata: [("GDDD", Encoding.ASCII.GetBytes("CYLS:100,HEADS:16,SECS:63,BPS:512\0"))])).MediaKind.Should().Be(ChdMediaKind.HardDisk);
        Read(ChdTestFile.Create("none", metadata: [])).MediaKind.Should().Be(ChdMediaKind.Unknown);
    }

    [Fact]
    public void OldBinaryCdMetadata_ShouldGiveTheTracks()
    {
        // CHCD: track count, then type, subtype, data size, subcode size, frames, extra frames per track
        var data = new byte[4 + 2 * 24];
        BinaryPrimitives.WriteUInt32BigEndian(data, 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4 + 16), 5000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(28), 7);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(28 + 16), 2000);

        var info = Read(ChdTestFile.Create("old", version: 3, metadata: [("CHCD", data)]));

        info.Tracks.Should().Equal(new ChdTrack(1, "MODE1_RAW", 5000), new ChdTrack(2, "AUDIO", 2000));
    }

    [Fact]
    public void InvalidFiles_ShouldBeRejectedWithAReason()
    {
        var valid = ChdTestFile.Create("x");
        var badVersion = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badVersion.AsSpan(12), 9);
        var looping = ChdTestFile.Create("loop", metadata: ChdTestFile.CdTracks(("AUDIO", 1)));
        BinaryPrimitives.WriteUInt64BigEndian(looping.AsSpan(124 + 8), 124);

        FluentActions.Invoking(() => Read(Encoding.ASCII.GetBytes("not a chd at all"))).Should().Throw<InvalidDataException>().WithMessage("Not a CHD*");
        FluentActions.Invoking(() => Read([])).Should().Throw<InvalidDataException>().WithMessage("Truncated CHD header");
        FluentActions.Invoking(() => Read(valid[..60])).Should().Throw<InvalidDataException>().WithMessage("Truncated CHD header");
        FluentActions.Invoking(() => Read(badVersion)).Should().Throw<InvalidDataException>().WithMessage("Unsupported CHD version 9");
        FluentActions.Invoking(() => Read(looping)).Should().Throw<InvalidDataException>().WithMessage("Invalid CHD metadata chain");
    }

    // =========================================================================
    // CHD disks of MAME-style DATs
    // =========================================================================

    private static string MameDat(string sha1) => $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>fmtowns_cd</name><description>FM Towns CD-ROMs</description></header>
          <game name="shadowb"><description>Shadow of the Beast</description><disk name="shadowb" sha1="{sha1}"/></game>
          <game name="nodumped"><description>Not dumped</description><disk name="nodumped" status="nodump"/></game>
          <game name="other"><description>Other</description><rom name="other.bin" size="4" crc="12345678"/><disk name="other" sha1="{ChdTestFile.Sha1Of("other")}"/></game>
        </datafile>
        """;

    [Fact]
    public void LogiqxDisks_ShouldBeReadWithoutTheUndumpedOnes()
    {
        var catalog = _services.DatService.ParseLogiqxXml(MameDat(ChdTestFile.Sha1Of("beast")));

        var disks = catalog.Entries.Where(e => e.IsDisk).ToList();
        disks.Select(d => d.Name).Should().Equal("shadowb", "other");
        disks[0].Sha1.Should().Be(ChdTestFile.Sha1Of("beast"));
        catalog.KnownSizes.Should().Equal([4L], "a disk has no size to look for");
    }

    [Fact]
    public void ClrMameProDisks_ShouldBeReadAndWrittenBack()
    {
        var sha1 = ChdTestFile.Sha1Of("beast");
        var catalog = _services.DatService.ParseClrMameProText($"""
            clrmamepro ( name "fmtowns_cd" )
            game ( name "shadowb" description "Shadow of the Beast" disk ( name "shadowb" sha1 {sha1} ) )
            game ( name "nodumped" disk ( name "nodumped" status nodump ) )
            """);

        catalog.Entries.Should().ContainSingle().Which.Should().Match<DatRomEntry>(e => e.IsDisk && e.Sha1 == sha1 && e.Name == "shadowb");
        catalog.ToClrMamePro().Should().Contain($"disk ( name \"shadowb\" sha1 {sha1} )");
        catalog.ToLogiqxXml().Should().Contain($"<disk name=\"shadowb\" sha1=\"{sha1}\"/>");
    }

    [Fact]
    public async Task MameDisk_ShouldIdentifyTheChdByTheSha1OfItsHeader()
    {
        WriteChd("Shadow of the Beast (1990)(Psygnosis).chd", ChdTestFile.Create("beast"));
        WriteChd("Unknown (1991)(Psygnosis).chd", ChdTestFile.Create("unknown"));
        var dat = _temp.WriteText("mame.dat", MameDat(ChdTestFile.Sha1Of("beast")));

        var result = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = dat,
            Pattern = string.Empty
        }, isDryRun: true);

        var beast = result.Items.Single(i => i.DisplayName.StartsWith("Shadow"));
        beast.Status.Should().Be(FileStatus.Valid);
        beast.DatGameName.Should().Be("shadowb");
        beast.TargetFilename.Should().Be("shadowb.chd", "a disk is named after the DAT, with the extension");
        beast.Parsed.Title.Should().Be("Shadow of the Beast", "a MAME short name is not a title: the file name is parsed");
        beast.Chd!.Sha1.Should().Be(ChdTestFile.Sha1Of("beast"));
        result.Items.Single(i => i.DisplayName.StartsWith("Unknown")).StatusReason!.Key.Should().Be("reason.unmatched_not_in_dat");
        result.MissingRoms.Select(r => r.Name).Should().BeEquivalentTo(["other.bin", "other"]);
        result.DatCatalog!.CreateFixDat(result.MissingRoms).ToLogiqxXml().Should().Contain($"<disk name=\"other\" sha1=\"{ChdTestFile.Sha1Of("other")}\"/>");
    }

    // =========================================================================
    // Redump DATs: optional match by name
    // =========================================================================

    private string RedumpDat() => _temp.WriteText("redump.dat", $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Fujitsu - FM Towns</name><description>Fujitsu - FM Towns (Redump)</description></header>
          <game name="{RedumpGame}"><description>{RedumpGame}</description>
            <rom name="{RedumpGame}.cue" size="900" crc="11111111"/>
            <rom name="{RedumpGame} (Track 1).bin" size="31399200" crc="22222222"/>
            <rom name="{RedumpGame} (Track 2).bin" size="55756512" crc="33333333"/>
          </game>
          <game name="Another Game (Japan)"><description>Another Game (Japan)</description>
            <rom name="Another Game (Japan).cue" size="100" crc="44444444"/>
          </game>
        </datafile>
        """);

    private Task<SortExecutionResult> SimulateRedumpAsync(bool matchByName) =>
        _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = RedumpDat(),
            MatchChdByName = matchByName
        }, isDryRun: true);

    [Fact]
    public async Task RedumpDat_WithoutMatchByName_ShouldKeepTheChdUnmatched()
    {
        WriteChd(RedumpGame + ".chd", ChdTestFile.Create("beast"));

        var result = await SimulateRedumpAsync(matchByName: false);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Unmatched, "a Redump DAT has no hash of the CHD (default behavior)");
        item.IsIdentifiedByName.Should().BeFalse();
        result.Stats.DatNameMatchedCount.Should().Be(0);
        result.MissingRoms.Should().HaveCount(4);
    }

    [Fact]
    public async Task RedumpDat_WithMatchByName_ShouldLinkTheChdToItsGameAsNotVerified()
    {
        WriteChd(RedumpGame + ".chd", ChdTestFile.Create("beast"));
        WriteChd("Not In The Dat (Japan).chd", ChdTestFile.Create("other"));

        var result = await SimulateRedumpAsync(matchByName: true);

        var item = result.Items.Single(i => i.DisplayName.StartsWith("Shadow"));
        item.Status.Should().Be(FileStatus.Valid);
        item.IsIdentifiedByName.Should().BeTrue();
        item.DatGameName.Should().Be(RedumpGame);
        item.TargetFilename.Should().Be(RedumpGame + ".chd");
        result.Items.Single(i => i.DisplayName.StartsWith("Not In")).Status.Should().Be(FileStatus.Unmatched);

        // The three entries of the game are neither matched nor missing
        result.Stats.DatMatchedCount.Should().Be(0);
        result.Stats.DatNameMatchedCount.Should().Be(3);
        result.Stats.DatMissingCount.Should().Be(1);
        result.Stats.DatCompletionPct.Should().Be(75.0);
        result.MissingRoms.Select(r => r.Name).Should().Equal("Another Game (Japan).cue");
    }

    [Fact]
    public async Task MultiDiscRedumpGame_WithEveryDiscAsAChdMatchedByName_ShouldBeKeptAsACompleteSet()
    {
        const string game = "Two Discs Game (Europe)";
        var dat = _temp.WriteText("redump-multi.dat", $"""
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sony - PlayStation</name><description>Sony - PlayStation (Redump)</description></header>
              <game name="{game} (Disc 1)"><description>{game} (Disc 1)</description>
                <rom name="{game} (Disc 1).cue" size="900" crc="11111111"/>
                <rom name="{game} (Disc 1) (Track 1).bin" size="31399200" crc="22222222"/>
                <rom name="{game} (Disc 1) (Track 2).bin" size="55756512" crc="33333333"/>
              </game>
              <game name="{game} (Disc 2)"><description>{game} (Disc 2)</description>
                <rom name="{game} (Disc 2).cue" size="901" crc="44444444"/>
                <rom name="{game} (Disc 2) (Track 1).bin" size="31399201" crc="55555555"/>
                <rom name="{game} (Disc 2) (Track 2).bin" size="55756513" crc="66666666"/>
              </game>
            </datafile>
            """);
        WriteChd(game + " (Disc 1).chd", ChdTestFile.Create("disc one"));
        WriteChd(game + " (Disc 2).chd", ChdTestFile.Create("disc two"));

        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = dat,
            MatchChdByName = true
        };
        config.Filters.CompleteMultidiskSetsOnly = true;
        config.Filters.NoIntroCompleteMultimediaSetsOnly = true;
        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Should().HaveCount(2);
        result.Items.Should().OnlyContain(i => i.IsIdentifiedByName);
        result.Items.Should().OnlyContain(
            i => i.Status == FileStatus.Valid,
            "a CHD stands for its whole disc: every disc of the set is there");
    }

    [Fact]
    public async Task DamagedChd_ShouldBeIsolatedWithItsReason_WithOrWithoutDat()
    {
        WriteChd(RedumpGame + ".chd", ChdTestFile.Create("beast")[..50]);

        var withDat = await SimulateRedumpAsync(matchByName: true);
        var withoutDat = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst")
        }, isDryRun: true);

        foreach (var item in new[] { withDat.Items.Single(), withoutDat.Items.Single() })
        {
            item.Status.Should().Be(FileStatus.Unmatched);
            item.StatusReason!.Key.Should().Be("reason.chd_invalid");
            _services.I18n.Format(item.StatusReason).Should().Be("Unreadable or incomplete CHD (Truncated CHD header)");
            item.IsIdentifiedByName.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ValidChd_WithoutDat_ShouldKeepItsInformationAndBeSortedByItsName()
    {
        WriteChd(RedumpGame + ".chd", ChdTestFile.Create("beast"));

        var result = await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            NamingConvention = NamingConvention.Redump
        }, isDryRun: true);

        var item = result.Items.Single();
        item.Status.Should().Be(FileStatus.Valid);
        item.Chd!.Tracks.Should().HaveCount(2);
    }

    [Fact]
    public void MediaLabels_ShouldExistInBothLanguages()
    {
        foreach (var locale in new[] { "fr", "en" })
        {
            var i18n = new CoreTestServices(locale).I18n;
            foreach (var kind in Enum.GetValues<ChdMediaKind>())
            {
                var key = $"chd.media_{kind.ToString().ToLowerInvariant()}";
                i18n.Tr(key).Should().NotBe(key, $"{key} is translated in {locale}");
            }
        }
    }
}
