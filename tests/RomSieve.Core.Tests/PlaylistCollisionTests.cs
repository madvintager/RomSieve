using System.Collections;
using FluentAssertions;
using RomSieve.Core.Models;
using RomSieve.Core.Services;
using RomSieve.Core.Tests.Fixtures;
using Xunit;

namespace RomSieve.Core.Tests;

/// <summary>
/// <c>.m3u</c> playlists follow the collision mode as any other target (an existing playlist is skipped, renamed or
/// replaced), and a cancellation while one is written stops the run without an error and without a partial file.
/// </summary>
public sealed class PlaylistCollisionTests : IDisposable
{
    private const string Disk1 = "Zak (1988)(Lucas)(Disk 1 of 2).adf";
    private const string Disk2 = "Zak (1988)(Lucas)(Disk 2 of 2).adf";
    private const string ExistingContent = "playlist of the user\n";

    private readonly TempDirectoryFixture _temp = new();
    private readonly CoreTestServices _services = new("en");
    private readonly List<LogMessage> _logs = [];
    private readonly string _src;
    private readonly string _dst;
    private readonly string _playlist;

    public PlaylistCollisionTests()
    {
        _src = _temp.CreateSubdirectory("src");
        _dst = _temp.CreateSubdirectory("dst");
        File.WriteAllText(Path.Combine(_src, Disk1), "disk 1");
        File.WriteAllText(Path.Combine(_src, Disk2), "disk 2");
        _playlist = Path.Combine(_dst, "Zak (1988)(Lucas).m3u");
    }

    public void Dispose() => _temp.Dispose();

    private SortConfig Config(CollisionMode mode) => new()
    {
        SourceDir = _src,
        DestinationDir = _dst,
        Pattern = string.Empty,
        CollisionMode = mode,
        GenerateM3u = true,
        SubfolderMultidisk = false,
        M3uPlacement = M3uPlacementMode.Subfolder
    };

    private Task<SortExecutionResult> RunAsync(CollisionMode mode, bool isDryRun = false) =>
        _services.Processor.ExecuteAsync(Config(mode), isDryRun, logProgress: new SynchronousProgress<LogMessage>(_logs.Add));

    private static readonly string[] Disks = [Disk1, Disk2];

    [Fact]
    public async Task NewPlaylist_ShouldListTheDisks()
    {
        await RunAsync(CollisionMode.Rename);

        File.ReadAllLines(_playlist).Should().Equal(Disks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPlaylist_InSkipMode_ShouldBeLeftAsItIs(bool isDryRun)
    {
        File.WriteAllText(_playlist, ExistingContent);

        await RunAsync(CollisionMode.Skip, isDryRun);

        File.ReadAllText(_playlist).Should().Be(ExistingContent);
        Directory.GetFiles(_dst, "*.m3u").Should().ContainSingle();
        _logs.Should().ContainSingle(l => l.Message.Key == "log.m3u_skipped_existing")
            .Which.Level.Should().Be(LogLevel.Skip);
        _logs.Should().NotContain(l => l.Message.Key == "log.m3u_created");
    }

    [Fact]
    public async Task ExistingPlaylist_InRenameMode_ShouldGetANewPlaylistBesideIt()
    {
        File.WriteAllText(_playlist, ExistingContent);

        await RunAsync(CollisionMode.Rename);

        File.ReadAllText(_playlist).Should().Be(ExistingContent);
        File.ReadAllLines(Path.Combine(_dst, "Zak (1988)(Lucas)_01.m3u")).Should().Equal(Disks);
    }

    [Fact]
    public async Task ExistingPlaylist_InOverwriteMode_ShouldBeReplaced()
    {
        File.WriteAllText(_playlist, ExistingContent);

        await RunAsync(CollisionMode.Overwrite);

        File.ReadAllLines(_playlist).Should().Equal(Disks);
        Directory.GetFiles(_dst, "*.m3u").Should().ContainSingle();
    }

    [Fact]
    public async Task PlaylistOnAPathTakenByAFileOfTheSort_ShouldFollowTheCollisionMode()
    {
        // A file of the source that the sort writes under the name of the playlist
        File.WriteAllText(Path.Combine(_src, "Zak (1988)(Lucas).m3u"), ExistingContent);

        await RunAsync(CollisionMode.Rename);

        File.ReadAllText(_playlist).Should().Be(ExistingContent, "the file of the sort keeps its name");
        File.ReadAllLines(Path.Combine(_dst, "Zak (1988)(Lucas)_01.m3u")).Should().Equal(Disks);
    }

    [Fact]
    public async Task CancellationWhileAPlaylistIsWritten_ShouldStopWithoutErrorNorPartialFile()
    {
        var config = Config(CollisionMode.Rename);
        var disks = Disks.Select(name => new ProcessItem
        {
            SourcePath = Path.Combine(_src, name),
            FileSize = 6,
            Parsed = _services.TosecParser.ParseFilename(name),
            Status = FileStatus.Valid,
            FinalTargetPath = Path.Combine(_dst, name)
        }).ToList();
        var executor = new FileTransferExecutor(config, isDryRun: false, _services.Organizer, _services.FilterService, _services.I18n,
            _services.OpticalCompanionService, _services.ChecksumService, (level, message) => _logs.Add(new LogMessage(level, message)));

        // The token is cancelled once the playlist has claimed its target, just before it is written
        using var cts = new CancellationTokenSource();
        var act = () => executor.GenerateM3uPlaylistsAsync([disks], new CancellingSet(cts), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _logs.Should().NotContain(l => l.Level == LogLevel.Error);
        Directory.EnumerateFileSystemEntries(_dst).Should().BeEmpty("neither the playlist nor its temporary file is left");
    }

    [Fact]
    public async Task CancelledRun_DuringThePlaylists_ShouldSayItWasCancelled()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<SortProgress>(p =>
        {
            if (p.Message.Key == "progress.writing_playlists")
            {
                cts.Cancel();
            }
        });

        var act = () => _services.Processor.ExecuteAsync(
            Config(CollisionMode.Rename), isDryRun: false, progress, new SynchronousProgress<LogMessage>(_logs.Add), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _logs.Should().Contain(l => l.Message.Key == "log.operation_cancelled_by_user");
        _logs.Should().NotContain(l => l.Message.Key == "log.m3u_error");
        File.Exists(_playlist).Should().BeFalse();
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Claimed paths that cancel <paramref name="cts"/> when a path is claimed.</summary>
    private sealed class CancellingSet(CancellationTokenSource cts) : ISet<string>
    {
        private readonly HashSet<string> _inner = new(StringComparer.OrdinalIgnoreCase);

        public bool Add(string item)
        {
            cts.Cancel();
            return _inner.Add(item);
        }

        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        void ICollection<string>.Add(string item) => Add(item);
        public void Clear() => _inner.Clear();
        public bool Contains(string item) => _inner.Contains(item);
        public void CopyTo(string[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
        public bool Remove(string item) => _inner.Remove(item);
        public IEnumerator<string> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void ExceptWith(IEnumerable<string> other) => _inner.ExceptWith(other);
        public void IntersectWith(IEnumerable<string> other) => _inner.IntersectWith(other);
        public bool IsProperSubsetOf(IEnumerable<string> other) => _inner.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => _inner.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => _inner.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => _inner.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => _inner.Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => _inner.SetEquals(other);
        public void SymmetricExceptWith(IEnumerable<string> other) => _inner.SymmetricExceptWith(other);
        public void UnionWith(IEnumerable<string> other) => _inner.UnionWith(other);
    }
}
