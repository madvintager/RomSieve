using System.IO;
using System.IO.Compression;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Tests ensuring that physical transfers honor cancellation without leaving partial files
/// and without ever touching the source.
/// </summary>
public sealed class CancellableTransferTests : IDisposable
{
    private const string BigGame = "Big Game (1990)(Ocean).adf";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");

    public void Dispose() => _temp.Dispose();

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static byte[] CreateContent(int size)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Runs a real sort in append mode and requests cancellation from the progress report
    /// emitted right before the first transfer starts (the append-mode log marks the start of the transfer phase).
    /// </summary>
    private async Task RunAndCancelAtFirstTransferAsync(SortConfig config)
    {
        Directory.CreateDirectory(config.DestinationDir);
        config.AppendToDestination = true;

        using var cts = new CancellationTokenSource();
        var transferPhase = false;
        var log = new SyncProgress<LogMessage>(msg =>
        {
            if (msg.Message.Key == "log.destination_append_mode")
            {
                transferPhase = true;
            }
        });
        var progress = new SyncProgress<SortProgress>(_ =>
        {
            if (transferPhase)
            {
                cts.Cancel();
            }
        });

        var act = () => _services.Processor.ExecuteAsync(config, isDryRun: false, progress, log, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Copy_CancelledWhileTransferringLargeFile_ShouldLeaveNoPartialFileAndKeepSource()
    {
        var content = CreateContent(8 * 1024 * 1024);
        var source = _temp.WriteBytes(Path.Combine("src", BigGame), content);
        var dst = Path.Combine(_temp.Path, "dst");

        await RunAndCancelAtFirstTransferAsync(new SortConfig
        {
            SourceDir = Path.Combine(_temp.Path, "src"),
            DestinationDir = dst,
            Pattern = string.Empty,
            TransferMode = TransferMode.Copy
        });

        File.Exists(Path.Combine(dst, BigGame)).Should().BeFalse();
        File.ReadAllBytes(source).Should().Equal(content);
    }

    [Fact]
    public async Task ZipExtraction_Cancelled_ShouldLeaveNoPartialFile()
    {
        var src = _temp.CreateSubdirectory("src");
        using (var zip = ZipFile.Open(Path.Combine(src, "big.zip"), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry(BigGame, CompressionLevel.NoCompression).Open();
            stream.Write(CreateContent(4 * 1024 * 1024));
        }
        var dst = Path.Combine(_temp.Path, "dst");

        await RunAndCancelAtFirstTransferAsync(new SortConfig
        {
            SourceDir = src,
            DestinationDir = dst,
            Pattern = string.Empty,
            TransferMode = TransferMode.Copy
        });

        File.Exists(Path.Combine(dst, BigGame)).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_ShouldPreserveLastWriteTime()
    {
        var source = _temp.WriteBytes(Path.Combine("src", BigGame), CreateContent(2 * 1024 * 1024));
        var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, timestamp);
        var dst = _temp.CreateSubdirectory("dst");

        await _services.Processor.ExecuteAsync(new SortConfig
        {
            SourceDir = Path.Combine(_temp.Path, "src"),
            DestinationDir = dst,
            Pattern = string.Empty,
            TransferMode = TransferMode.Copy
        }, isDryRun: false);

        var target = Path.Combine(dst, BigGame);
        File.ReadAllBytes(target).Should().Equal(File.ReadAllBytes(source));
        File.GetLastWriteTimeUtc(target).Should().Be(timestamp);
    }

    [Fact]
    public async Task CopyFileAsync_CancelledToken_ShouldDeleteNothingButThrow()
    {
        var source = _temp.WriteBytes("source.bin", CreateContent(1024));
        var target = _temp.GetFilePath("target.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => FileTransferExecutor.CopyFileAsync(source, target, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(source).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\roms\game.adf", @"C:\sorted\game.adf", true)]
    [InlineData(@"C:\roms\game.adf", @"D:\sorted\game.adf", false)]
    [InlineData(@"c:\roms\game.adf", @"C:\sorted\game.adf", true)]
    public void AreOnSameVolume_WindowsDriveRoots(string source, string target, bool expected)
    {
        VolumeLocator.AreOnSameVolume(source, target, [@"C:\", @"D:\"], ignoreCase: true)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("/home/user/roms/game.adf", "/home/user/sorted/game.adf", true)]
    [InlineData("/home/user/roms/game.adf", "/mnt/nas/sorted/game.adf", false)]
    [InlineData("/mnt/nas/roms/game.adf", "/mnt/nas/sorted/game.adf", true)]
    [InlineData("/mnt/nasty/game.adf", "/mnt/nas/game.adf", false)]
    public void AreOnSameVolume_UnixMountPoints_ShouldUseLongestMatchingMountPoint(string source, string target, bool expected)
    {
        VolumeLocator.AreOnSameVolume(source, target, ["/", "/mnt/nas"], ignoreCase: false)
            .Should().Be(expected);
    }

    [Fact]
    public void AreOnSameVolume_UnknownVolume_ShouldBeTreatedAsDifferent()
    {
        VolumeLocator.AreOnSameVolume(@"X:\a.adf", @"X:\b.adf", [@"C:\"], ignoreCase: true)
            .Should().BeFalse();
    }
}
