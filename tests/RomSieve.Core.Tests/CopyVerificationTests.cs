using System.IO;
using System.IO.Compression;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;

namespace RomSieve.Core.Tests;

/// <summary>
/// Optional verification after copy: a target whose CRC32 differs from the data read from the source is deleted,
/// the item is in error, the source is kept (even for a move) and nothing is journaled.
/// </summary>
public sealed class CopyVerificationTests : IDisposable
{
    private const string RomName = "Game (1990)(Pub)(FR).adf";
    private static readonly byte[] Content = [.. Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7))];

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly List<LocalizedMessage> _logs = [];

    public void Dispose() => _temp.Dispose();

    private sealed record TransferRun(ProcessItem Item, string Target, int JournaledOperations);

    /// <summary>
    /// Transfers one item through a <see cref="FileTransferExecutor"/> whose targets are corrupted, or not,
    /// right after being written. Every volume is unknown, so a move goes through a copy.
    /// </summary>
    private async Task<TransferRun> TransferAsync(
        TransferMode mode,
        bool verify,
        bool corrupt,
        string? archivePath = null,
        string? archiveMember = null)
    {
        var source = archivePath ?? _temp.WriteBytes(Path.Combine("src", RomName), Content);
        var target = Path.Combine(_temp.Path, "dst", RomName);
        var config = new SortConfig
        {
            SourceDir = Path.Combine(_temp.Path, "src"),
            DestinationDir = Path.Combine(_temp.Path, "dst"),
            TransferMode = mode,
            VerifyCopies = verify
        };
        var journalService = new OperationJournalService(_temp.GetFilePath("journals"), _services.ChecksumService, _services.CrcCache);
        await using var journal = await journalService.BeginAsync(config);

        var executor = new FileTransferExecutor(
            config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService, (_, message) => _logs.Add(message), journal)
        {
            VolumeRoots = [],
            AfterTargetWritten = path =>
            {
                if (corrupt)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                    stream.WriteByte(0xFF);
                }
            }
        };

        var item = new ProcessItem
        {
            SourcePath = source,
            FileSize = Content.Length,
            Parsed = _services.TosecParser.ParseFilename(RomName),
            ArchivePath = archivePath,
            ArchiveMember = archiveMember,
            FinalTargetPath = target
        };
        await executor.PerformTransferAsync(item);
        return new TransferRun(item, target, journal.OperationCount);
    }

    private string CreateZip()
    {
        var zipPath = _temp.GetFilePath(Path.Combine("src", "game.zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var stream = zip.CreateEntry(RomName).Open();
        stream.Write(Content);
        return zipPath;
    }

    private static void ShouldHaveFailedVerification(TransferRun run)
    {
        run.Item.Status.Should().Be(FileStatus.Error);
        run.Item.StatusReason!.Key.Should().Be("reason.copy_verification_failed");
        File.Exists(run.Target).Should().BeFalse("a corrupted copy is deleted");
        run.JournaledOperations.Should().Be(0, "a failed copy is not journaled");
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public async Task VerifiedTransfer_WithoutCorruption_ShouldSucceedAndBeJournaled(TransferMode mode)
    {
        var run = await TransferAsync(mode, verify: true, corrupt: false);

        run.Item.Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(run.Target).Should().Equal(Content);
        run.JournaledOperations.Should().Be(1);
    }

    [Fact]
    public async Task VerifiedCopy_OfACorruptedTarget_ShouldDeleteTheTargetAndKeepTheSource()
    {
        var run = await TransferAsync(TransferMode.Copy, verify: true, corrupt: true);

        ShouldHaveFailedVerification(run);
        File.ReadAllBytes(run.Item.SourcePath).Should().Equal(Content);
        _logs.Should().Contain(m => m.Key == "log.copy_verification_failed");
    }

    [Fact]
    public async Task VerifiedMoveAcrossVolumes_OfACorruptedTarget_ShouldKeepTheSource()
    {
        var run = await TransferAsync(TransferMode.Move, verify: true, corrupt: true);

        ShouldHaveFailedVerification(run);
        File.ReadAllBytes(run.Item.SourcePath).Should().Equal(Content, "the source of a move is only deleted after a verified copy");
    }

    [Fact]
    public async Task VerifiedExtraction_OfACorruptedTarget_ShouldDeleteTheTarget()
    {
        var zip = CreateZip();

        var run = await TransferAsync(TransferMode.Copy, verify: true, corrupt: true, zip, RomName);

        ShouldHaveFailedVerification(run);
        File.Exists(zip).Should().BeTrue();
    }

    [Fact]
    public async Task VerifiedExtraction_WithoutCorruption_ShouldSucceed()
    {
        var run = await TransferAsync(TransferMode.Copy, verify: true, corrupt: false, CreateZip(), RomName);

        run.Item.Status.Should().Be(FileStatus.Valid);
        File.ReadAllBytes(run.Target).Should().Equal(Content);
    }

    [Fact]
    public async Task UnverifiedCopy_ShouldKeepTheDefaultBehavior()
    {
        var run = await TransferAsync(TransferMode.Copy, verify: false, corrupt: true);

        run.Item.Status.Should().Be(FileStatus.Valid, "the verification is disabled by default");
        File.Exists(run.Target).Should().BeTrue();
        run.JournaledOperations.Should().Be(1);
    }

    [Fact]
    public async Task VerificationReason_ShouldBeTranslatedInBothLanguages()
    {
        var run = await TransferAsync(TransferMode.Copy, verify: true, corrupt: true);

        var fr = new CoreTestServices("fr").I18n.Format(run.Item.StatusReason!);
        var en = _services.I18n.Format(run.Item.StatusReason!);
        fr.Should().StartWith("Vérification après copie échouée");
        en.Should().StartWith("Verification after copy failed");
        en.Should().Contain(run.Item.StatusReason!.Args["expected"]!.ToString());
    }

    [Fact]
    public void SortConfig_ShouldNotVerifyCopiesByDefault()
    {
        new SortConfig { SourceDir = "a", DestinationDir = "b" }.VerifyCopies.Should().BeFalse();
    }
}
