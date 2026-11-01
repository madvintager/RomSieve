using System.IO;
using System.Text;
using FluentAssertions;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// Files written in one step (configuration, profiles, caches): whatever happens while writing, the target holds either
/// its previous content or its new one, never a truncated file, and no temporary file is left behind.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly TempDirectoryFixture _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Files of the test folder other than the target: the temporary files left behind.</summary>
    private string[] OtherFiles(string target) =>
        Directory.GetFiles(_temp.Path).Where(f => f != target).ToArray();

    [Fact]
    public void WriteAllText_ShouldCreateTheFile()
    {
        var target = _temp.GetFilePath("config.json");

        AtomicFile.WriteAllText(target, "{\"a\": 1}", Utf8);

        File.ReadAllBytes(target).Should().Equal(Utf8.GetBytes("{\"a\": 1}"), "no byte order mark");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public void WriteAllText_WithAByteOrderMark_ShouldWriteIt()
    {
        var target = _temp.GetFilePath("report.csv");

        AtomicFile.WriteAllText(target, "é", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        File.ReadAllBytes(target).Should().Equal(0xEF, 0xBB, 0xBF, 0xC3, 0xA9);
    }

    [Fact]
    public async Task WriteAllText_ShouldReplaceAnExistingFileWhole()
    {
        var target = _temp.WriteText("config.json", new string('x', 10_000));

        AtomicFile.WriteAllText(target, "short", Utf8);
        File.ReadAllText(target).Should().Be("short");

        await AtomicFile.WriteAllTextAsync(target, "shorter", Utf8);
        File.ReadAllText(target).Should().Be("shorter");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public void Write_WhenWritingFails_ShouldKeepThePreviousFile()
    {
        var target = _temp.WriteText("config.json", "previous");

        var act = () => AtomicFile.Write(target, stream =>
        {
            stream.Write("partial"u8);
            throw new IOException("disk full");
        });

        act.Should().Throw<IOException>().WithMessage("disk full");
        File.ReadAllText(target).Should().Be("previous");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_WhenWritingFails_ShouldKeepThePreviousFile()
    {
        var target = _temp.WriteText("crc_cache.json", "previous");

        var act = () => AtomicFile.WriteAsync(target, async (stream, ct) =>
        {
            await stream.WriteAsync("partial"u8.ToArray(), ct);
            throw new IOException("disk full");
        });

        await act.Should().ThrowAsync<IOException>();
        File.ReadAllText(target).Should().Be("previous");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAllTextAsync_Cancelled_ShouldKeepThePreviousFile()
    {
        var target = _temp.WriteText("crc_cache.json", "previous");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => AtomicFile.WriteAllTextAsync(target, "new", Utf8, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(target).Should().Be("previous");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public void Write_WhenTheTargetCannotBeReplaced_ShouldThrowAndLeaveNoTemporaryFile()
    {
        // A folder where the file should be
        var target = _temp.CreateSubdirectory("config.json");

        var act = () => AtomicFile.WriteAllText(target, "new", Utf8);

        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        Directory.Exists(target).Should().BeTrue();
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAllTextAsync_SeveralAtOnce_ShouldLeaveOneWholeVersion()
    {
        var target = _temp.WriteText("config.json", "previous");
        var versions = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 50_000)).ToArray();

        await Task.WhenAll(versions.Select(v => Task.Run(() => AtomicFile.WriteAllTextAsync(target, v, Utf8))));

        versions.Should().Contain(File.ReadAllText(target));
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public void WriteAllText_ThroughASymbolicLink_ShouldWriteTheLinkedFileAndKeepTheLink()
    {
        var real = _temp.WriteText(Path.Combine("dotfiles", "config.json"), "previous");
        var link = _temp.GetFilePath("config.json");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the right to create links (developer mode off)
            return;
        }

        AtomicFile.WriteAllText(link, "new", Utf8);

        new FileInfo(link).LinkTarget.Should().NotBeNull("the link is still a link");
        File.ReadAllText(real).Should().Be("new");
        Directory.GetFiles(_temp.GetFilePath("dotfiles")).Should().ContainSingle();
    }

    [Fact]
    public void WriteAllText_ShouldKeepTheUnixPermissionsOfThePreviousFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = _temp.WriteText("config.json", "previous");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AtomicFile.WriteAllText(target, "new", Utf8);

        File.GetUnixFileMode(target).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task ConfigurationService_ShouldSaveInOneStep()
    {
        var target = _temp.WriteText("config.json", "{\"old\": true}");
        var service = new ConfigurationService();

        (await service.SaveConfigToFileAsync(new Dictionary<string, object?> { ["new"] = 1 }, target)).Should().BeTrue();

        (await service.LoadConfigFromFileAsync(target)).Should().ContainKey("new").And.NotContainKey("old");
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigurationService_WhenTheFileCannotBeReplaced_ShouldReturnFalse()
    {
        var target = _temp.CreateSubdirectory("config.json");

        var saved = await new ConfigurationService().SaveConfigToFileAsync(new Dictionary<string, object?> { ["a"] = 1 }, target);

        saved.Should().BeFalse();
        OtherFiles(target).Should().BeEmpty();
    }

    [Fact]
    public async Task CrcCache_CancelledSave_ShouldKeepThePreviousFile()
    {
        var cacheFile = _temp.GetFilePath("crc_cache.json");
        var rom = _temp.WriteBytes(Path.Combine("roms", "game.bin"), "ROM"u8.ToArray());
        var cache = new CrcCacheService(cacheFile);
        cache.Set(rom, "12345678");
        await cache.SaveAsync();
        var saved = File.ReadAllText(cacheFile);

        var other = _temp.WriteBytes(Path.Combine("roms", "other.bin"), "OTHER"u8.ToArray());
        cache.Set(other, "87654321");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await cache.SaveAsync(cts.Token);

        File.ReadAllText(cacheFile).Should().Be(saved);
        OtherFiles(cacheFile).Should().BeEmpty();

        // Still dirty: the next save writes the new entry
        await cache.SaveAsync();
        new CrcCacheService(cacheFile).Get(other).Should().Be("87654321");
    }
}
