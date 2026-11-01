using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Verification of the content of the archives (<see cref="SortConfig.VerifyArchiveContents"/>): every member is
/// decompressed and compared with the CRC32 its archive records, which alone identifies it otherwise. A member, or a
/// MAME set, whose data does not match is isolated as damaged; real 7z and RAR archives, solid or not, are intact.
/// </summary>
public sealed class ArchiveContentTests : IDisposable
{
    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    private static readonly byte[] GoodData = Encoding.ASCII.GetBytes("GOOD GAME DATA, STORED WITHOUT COMPRESSION");
    private static readonly byte[] DamagedData = Encoding.ASCII.GetBytes("DAMAGED GAME DATA, STORED WITHOUT COMPRESSION");
    private const string GoodName = "Good Game (1990)(Ocean).adf";
    private const string DamagedName = "Damaged Game (1991)(Ocean).adf";

    public void Dispose() => _temp.Dispose();

    private static string Crc(byte[] data) => Crc32.HashToUInt32(data).ToString("x8");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archives", name);

    /// <summary>
    /// A ZIP archive whose members are stored as they are, and in which the data of <paramref name="damaged"/> is then
    /// altered: its header keeps the CRC32 of the right data.
    /// </summary>
    private string WriteZip(string relativePath, (string Name, byte[] Data)[] members, string? damaged = null)
    {
        var path = _temp.GetFilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in members)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(data);
            }
        }

        if (damaged is not null)
        {
            var data = members.Single(m => m.Name == damaged).Data;
            var bytes = File.ReadAllBytes(path);
            var at = bytes.AsSpan().IndexOf(data);
            at.Should().BePositive("the member is stored without compression");
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }

        return path;
    }

    [Theory]
    [InlineData("roms_solid.7z")]
    [InlineData("roms_nonsolid.7z")]
    [InlineData("sharpcompress_rar4.rar")]
    [InlineData("sharpcompress_rar5.rar")]
    public void RealArchives_ShouldBeFoundIntact(string fixture)
    {
        _services.ChecksumService.FindDamagedMembers(Fixture(fixture)).Should().BeEmpty();
    }

    [Fact]
    public void DamagedZip_ShouldNameTheMemberWhoseDataDoesNotMatchItsCrc()
    {
        var zip = WriteZip("pack.zip", [(GoodName, GoodData), (DamagedName, DamagedData)], damaged: DamagedName);

        var damaged = _services.ChecksumService.FindDamagedMembers(zip);

        damaged.Should().ContainSingle().Which.MemberName.Should().Be(DamagedName);
        damaged[0].ExpectedCrc32.Should().Be(Crc(DamagedData));
        (damaged[0].ActualCrc32 is not null || damaged[0].Error is not null).Should().BeTrue();
    }

    [Fact]
    public void UnreadableArchive_ShouldBeRefusedAsInvalidData()
    {
        var archive = _temp.WriteText("broken.7z", "not an archive");

        var act = () => _services.ChecksumService.FindDamagedMembers(archive);

        act.Should().Throw<Exception>().Which.Should().Match<Exception>(ex => ex is InvalidDataException || ex is IOException);
    }

    private static string Game((string Name, byte[] Data) rom) =>
        $"""  <game name="{Path.GetFileNameWithoutExtension(rom.Name)}"><description>{rom.Name}</description><rom name="{rom.Name}" size="{rom.Data.Length}" crc="{Crc(rom.Data)}"/></game>""";

    private string WriteDat(params (string Name, byte[] Data)[] roms) => _temp.WriteText("set.dat", $"""
        <?xml version="1.0"?>
        <datafile>
          <header><name>Test set</name><description>Test set</description><version>1</version></header>
        {string.Join(Environment.NewLine, roms.Select(Game))}
        </datafile>
        """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedMember_ShouldOnlyBeIsolatedWhenTheContentIsVerified(bool verify)
    {
        WriteZip(Path.Combine("src", "pack.zip"), [(GoodName, GoodData), (DamagedName, DamagedData)], damaged: DamagedName);
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = WriteDat((GoodName, GoodData), (DamagedName, DamagedData)),
            VerifyArchiveContents = verify
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Single(it => it.ArchiveMember == GoodName).Status.Should().Be(FileStatus.Valid);
        var damaged = result.Items.Single(it => it.ArchiveMember == DamagedName);
        if (!verify)
        {
            damaged.Status.Should().Be(FileStatus.Valid, "the CRC32 of the header alone identifies it");
            result.Stats.DatMatchedCount.Should().Be(2);
            return;
        }

        damaged.Status.Should().Be(FileStatus.Unmatched);
        damaged.StatusReason!.Key.Should().Be("reason.archive_member_damaged");
        _services.I18n.Format(damaged.StatusReason).Should().StartWith($"File damaged in its archive: {DamagedName} (");
        result.Stats.DatMatchedCount.Should().Be(1, "a damaged file is not in the collection");
    }

    [Fact]
    public async Task DamagedMember_WithoutDat_ShouldBeIsolatedToo()
    {
        WriteZip(Path.Combine("src", "pack.zip"), [(GoodName, GoodData), (DamagedName, DamagedData)], damaged: DamagedName);
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            VerifyArchiveContents = true
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        result.Items.Single(it => it.ArchiveMember == GoodName).Status.Should().Be(FileStatus.Valid);
        result.Items.Single(it => it.ArchiveMember == DamagedName).StatusReason!.Key.Should().Be("reason.archive_member_damaged");
    }

    [Fact]
    public async Task DamagedTrack_ShouldMakeItsWholeDiscDamaged()
    {
        var cue = Encoding.ASCII.GetBytes("FILE \"Disc (Track 1).bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n");
        var track = Encoding.ASCII.GetBytes("TRACK DATA OF THE DISC, STORED WITHOUT COMPRESSION");
        WriteZip(Path.Combine("src", "disc.zip"), [("Disc (1995)(Sega).cue", cue), ("Disc (Track 1).bin", track)], damaged: "Disc (Track 1).bin");
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            VerifyArchiveContents = true
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var disc = result.Items.Single(it => it.ArchiveMember == "Disc (1995)(Sega).cue");
        disc.Status.Should().Be(FileStatus.Unmatched);
        disc.StatusReason!.Key.Should().Be("reason.archive_member_damaged");
        disc.RelatedItems.Should().ContainSingle(r => r.ArchiveMember == "Disc (Track 1).bin");
    }

    [Fact]
    public async Task DamagedMameSet_ShouldBeIsolatedAndItsRomsMissing()
    {
        var program = Encoding.ASCII.GetBytes("ATARI TETRIS PROGRAM, STORED WITHOUT COMPRESSION");
        var sound = Encoding.ASCII.GetBytes("ATARI TETRIS SOUND, STORED WITHOUT COMPRESSION");
        WriteZip(Path.Combine("src", "atetris.zip"), [("atetris.p", program), ("atetris.s", sound)], damaged: "atetris.s");
        var dat = _temp.WriteText("mame.xml", $"""
            <?xml version="1.0"?>
            <mame build="0.289 (test)">
            	<machine name="atetris"><description>Tetris (set 1)</description><year>1988</year><manufacturer>Atari Games</manufacturer><rom name="atetris.p" size="{program.Length}" crc="{Crc(program)}"/><rom name="atetris.s" size="{sound.Length}" crc="{Crc(sound)}"/><driver status="good"/></machine>
            </mame>
            """);
        var config = new SortConfig
        {
            SourceDir = _temp.GetFilePath("src"),
            DestinationDir = _temp.GetFilePath("dst"),
            DatFile = dat,
            Pattern = string.Empty,
            VerifyArchiveContents = true
        };

        var result = await _services.Processor.ExecuteAsync(config, isDryRun: true);

        var set = result.Items.Should().ContainSingle().Which;
        set.Status.Should().Be(FileStatus.Unmatched);
        _services.I18n.Format(set.StatusReason).Should()
            .StartWith("Damaged archive: 1 file(s) do not match their CRC32, among which atetris.s (")
            .And.EndWith("Repair the set with a ROM manager (clrmamepro, RomVault).");
        result.Stats.DatMatchedCount.Should().Be(0);

        // Without the option, the set is sorted as it is
        config.VerifyArchiveContents = false;
        (await _services.Processor.ExecuteAsync(config, isDryRun: true)).Items.Single().Status.Should().Be(FileStatus.Valid);
    }
}
