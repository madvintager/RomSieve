using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// 7z and RAR archives (SharpCompress), read like ZIP archives: members listed with the size and CRC32 of their headers,
/// optical descriptors linked to their tracks, identification against a DAT, extraction (also verified), journal and
/// undo, the archive staying in place. The archives are real files of <c>Fixtures/Archives</c> (see its README).
/// </summary>
public sealed class ArchiveFormatTests : IDisposable
{
    private const string Alpha = "Alpha (1990)(Ocean).adf";
    private const string Beta = "Beta (1991)(Ocean).adf";
    private const string Cue = "Disc (1995)(Sony).cue";
    private const string Track = "Disc (1995)(Sony) (Track 1).bin";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    /// <summary>Contents of the files of the 7z fixtures: a pattern repeated to a length.</summary>
    internal static byte[] Pattern(string pattern, int length)
    {
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(pattern);
        }

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    private static readonly Dictionary<string, byte[]> SevenZipContents = new()
    {
        [Alpha] = Pattern("ALPHA-", 8192),
        [Beta] = Pattern("BETA--", 8192),
        [Track] = Pattern("TRACK1", 4704),
        [Cue] = Encoding.ASCII.GetBytes("FILE \"Disc (1995)(Sony) (Track 1).bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n")
    };

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8", CultureInfo.InvariantCulture);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", name);

    /// <summary>Copies a fixture archive into the source directory of the test.</summary>
    private string AddToSource(string fixture, string? name = null)
    {
        var target = _temp.GetFilePath(Path.Combine("src", name ?? fixture));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Fixture(fixture), target);
        return target;
    }

    /// <summary>TOSEC DAT of the files of the 7z fixtures.</summary>
    private string SevenZipDat() => _temp.WriteText("roms.dat", $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Commodore Amiga - Games - [ADF] (TOSEC)</name><description>TOSEC test DAT</description></header>
          <game name="Alpha (1990)(Ocean)"><description>Alpha</description><rom name="{Alpha}" size="8192" crc="{Crc(SevenZipContents[Alpha])}"/></game>
          <game name="Beta (1991)(Ocean)"><description>Beta</description><rom name="{Beta}" size="8192" crc="{Crc(SevenZipContents[Beta])}"/></game>
          <game name="Disc (1995)(Sony)"><description>Disc</description>
            <rom name="{Cue}" size="{SevenZipContents[Cue].Length}" crc="{Crc(SevenZipContents[Cue])}"/>
            <rom name="{Track}" size="4704" crc="{Crc(SevenZipContents[Track])}"/>
          </game>
        </datafile>
        """);

    private SortConfig Config(TransferMode mode = TransferMode.Copy, string? dat = null) => new()
    {
        SourceDir = _temp.GetFilePath("src"),
        DestinationDir = _temp.GetFilePath("dst"),
        Pattern = string.Empty,
        TransferMode = mode,
        DatFile = dat
    };

    [Theory]
    [InlineData("roms.zip", true)]
    [InlineData("roms.7z", true)]
    [InlineData("ROMS.RAR", true)]
    [InlineData("roms.tar", false)]
    [InlineData("game.adf", false)]
    public void IsSupportedArchive_ShouldRecognizeZip7zAndRar(string name, bool expected)
    {
        _services.ChecksumService.IsSupportedArchive(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("roms_solid.7z")]
    [InlineData("roms_nonsolid.7z")]
    public void SevenZipMembers_ShouldBeListedWithTheSizeAndCrcOfTheirHeaders(string fixture)
    {
        var members = _services.ChecksumService.GetArchiveMembers(Fixture(fixture));

        members.Select(m => (m.MemberName, m.Size, m.Crc32)).Should().BeEquivalentTo(
            SevenZipContents.Select(kv => (kv.Key, (long)kv.Value.Length, Crc(kv.Value))));
    }

    [Theory]
    [InlineData("sharpcompress_rar4.rar")]
    [InlineData("sharpcompress_rar5.rar")]
    public void RarMembers_ShouldBeListedWithoutTheDirectories_AndWithUnicodeNames(string fixture)
    {
        var members = _services.ChecksumService.GetArchiveMembers(Fixture(fixture));

        members.Select(m => (Path.GetFileName(m.MemberName.Replace('\\', '/')), m.Size, m.Crc32)).Should().BeEquivalentTo(
            [("тест.txt", 15498L, "9bd160fa"), ("test.exe", 45056L, "cfb109c8"), ("test.jpg", 40372L, "088814e3")]);
    }

    [Theory]
    [InlineData("sharpcompress_rar4.rar")]
    [InlineData("sharpcompress_rar5.rar")]
    [InlineData("roms_solid.7z")]
    public async Task ArchiveMember_ShouldBeDecompressedWithTheCrcOfItsHeader(string fixture)
    {
        foreach (var member in _services.ChecksumService.GetArchiveMembers(Fixture(fixture)))
        {
            await using var stream = _services.ChecksumService.OpenArchiveMember(Fixture(fixture), member.MemberName);
            using var data = new MemoryStream();
            await stream.CopyToAsync(data);

            data.Length.Should().Be(member.Size);
            Crc(data.ToArray()).Should().Be(member.Crc32).And.Be(stream.ExpectedCrc32);
        }
    }

    [Theory]
    [InlineData("roms_solid.7z")]
    [InlineData("roms_nonsolid.7z")]
    public async Task SevenZipArchive_ShouldBeScannedAndIdentifiedLikeAZip(string fixture)
    {
        AddToSource(fixture);

        var result = await _services.Processor.ExecuteAsync(Config(dat: SevenZipDat()), isDryRun: true);

        result.Items.Select(i => i.ArchiveMember).Should().BeEquivalentTo([Alpha, Beta, Cue], "the track is attached to its descriptor");
        result.Items.Should().OnlyContain(i => i.Status == FileStatus.Valid && i.ArchivePath!.EndsWith(fixture));
        result.Items.Single(i => i.ArchiveMember == Cue).RelatedItems.Select(r => r.ArchiveMember).Should().Equal(Track);
        result.Stats.DatMatchedCount.Should().Be(4);
        result.MissingRoms.Should().BeEmpty();
    }

    [Theory]
    [InlineData("roms_solid.7z", TransferMode.Copy)]
    [InlineData("roms_nonsolid.7z", TransferMode.Move)]
    [InlineData("roms_solid.7z", TransferMode.HardLink)]
    public async Task SevenZipMembers_ShouldBeExtractedAndTheArchiveKeptInPlace(string fixture, TransferMode mode)
    {
        var archive = AddToSource(fixture);
        var config = Config(mode, SevenZipDat());
        config.VerifyCopies = true;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: false);

        result.Items.Should().OnlyContain(i => i.Status == FileStatus.Valid);
        foreach (var (name, data) in SevenZipContents)
        {
            File.ReadAllBytes(_temp.GetFilePath(Path.Combine("dst", name))).Should().Equal(data, $"{name} is extracted (and verified)");
        }

        File.ReadAllBytes(archive).Should().Equal(File.ReadAllBytes(Fixture(fixture)), "the archive itself is never moved nor modified");
    }

    [Theory]
    [InlineData("sharpcompress_rar4.rar")]
    [InlineData("sharpcompress_rar5.rar")]
    public async Task RarMembers_ShouldBeExtractedIntoUnmatchedWithoutDat(string fixture)
    {
        AddToSource(fixture);

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: false);

        result.Items.Should().HaveCount(3).And.OnlyContain(i => i.Status == FileStatus.Unmatched);
        var jpg = Directory.GetFiles(_temp.GetFilePath("dst"), "test.jpg", SearchOption.AllDirectories).Single();
        Crc(File.ReadAllBytes(jpg)).Should().Be("088814e3");
    }

    [Fact]
    public async Task SevenZipExtraction_ShouldBeJournaledAndUndone()
    {
        var archive = AddToSource("roms_solid.7z");
        var crcCache = new CrcCacheService(_temp.GetFilePath(Path.Combine("cache", "crc_cache.json")));
        var journal = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, crcCache);
        var processor = new SortProcessorService(
            _services.ChecksumService, _services.DatService, _services.NoIntroParser, _services.FilterService,
            _services.Organizer, _services.I18n, _services.OpticalCompanionService, _services.MameParser, crcCache, journal);

        await processor.ExecuteAsync(Config(TransferMode.Move, SevenZipDat()), isDryRun: false);
        File.Exists(_temp.GetFilePath(Path.Combine("dst", Alpha))).Should().BeTrue();

        var report = await journal.UndoAsync(journal.FindLastUndoableJournal()!.Path);

        report.Issues.Should().BeEmpty();
        Directory.GetFiles(_temp.GetFilePath("dst"), "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(archive).Should().BeTrue("an extraction leaves its archive in place, even in move mode");
    }

    [Fact]
    public async Task Sha1Verification_ShouldDecompressASevenZipMember()
    {
        AddToSource("roms_solid.7z");
        var sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(SevenZipContents[Alpha]));
        var dat = SevenZipDat().Replace($"crc=\"{Crc(SevenZipContents[Alpha])}\"", $"crc=\"{Crc(SevenZipContents[Alpha])}\" sha1=\"{sha1}\"");
        var config = Config(dat: _temp.WriteText("sha1.dat", File.ReadAllText(dat)));
        config.VerifySha1 = true;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Single(i => i.ArchiveMember == Alpha).Status.Should().Be(FileStatus.Valid);
    }

    [Fact]
    public async Task SevenZipArchive_WithoutArchiveScanning_ShouldBeAPlainFile()
    {
        AddToSource("roms_solid.7z");
        var config = Config();
        config.Filters.ScanArchives = false;

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Should().ContainSingle().Which.ArchiveMember.Should().BeNull();
    }

    [Fact]
    public async Task DamagedSevenZipArchive_ShouldBeAPlainFileWithoutStoppingTheSort()
    {
        var bytes = File.ReadAllBytes(Fixture("roms_solid.7z"));
        _temp.WriteBytes(Path.Combine("src", "damaged.7z"), bytes[..(bytes.Length / 2)]);
        _temp.WriteText(Path.Combine("src", Beta), "beta");

        var result = await _services.Processor.ExecuteAsync(Config(), isDryRun: true);

        result.Items.Select(i => i.DisplayName).Should().BeEquivalentTo(["damaged.7z", Beta]);
    }
}
