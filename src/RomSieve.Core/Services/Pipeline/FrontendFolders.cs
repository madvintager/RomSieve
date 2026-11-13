using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Folder of the platform of each file for the frontend of a run (<see cref="SortConfig.Frontend"/>), created by
/// <see cref="SortProcessorService"/> for a run with a frontend and read by <see cref="FileTransferExecutor.PlacementOf"/>.
/// <list type="bullet">
/// <item>The folder comes from the frontend table (<see cref="IFrontendPlatformService.FindFolder"/>); a DAT the table
/// does not know, or whose platform the frontend has no folder for, keeps its name as folder, as with a folder per DAT,
/// with a warning once per DAT. A MAME software list gets no folder: its software already goes in the folder of its list.</item>
/// <item>A folder of the destination root whose name only differs by case is taken as it is (<c>GAMEBOY</c> for
/// <c>Gameboy</c>): the root is listed once per run, at the first file.</item>
/// <item>An archive the frontend does not read in the folder of its platform gets a warning once per DAT and extension
/// (<see cref="NoteWritten"/>); nothing is refused, the emulator may be set up otherwise.</item>
/// <item>A folder that receives the files of several DATs (<c>Nintendo - Game Boy</c> and its <c>(Private)</c> DAT, a
/// No-Intro and a TOSEC DAT of a system) is reported once at the end (<see cref="ReportSharedFolders"/>): the 1G1R being
/// computed DAT by DAT, a game of two of them may be written twice there.</item>
/// </list>
/// </summary>
internal sealed class FrontendFolders
{
    // Extensions of the archives a sort writes: ZIP mode, MAME sets, preserved source archives
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    private readonly IFrontendPlatformService _platforms;
    private readonly IPathOrganizerService _organizer;
    private readonly FrontendTarget _frontend;
    private readonly string _destinationDir;
    private readonly Action<LogLevel, LocalizedMessage> _log;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    // DATs whose files each folder of a platform receives
    private readonly Dictionary<string, List<string>> _datsByFolder = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _folders;

    public FrontendFolders(
        IFrontendPlatformService platforms,
        IPathOrganizerService organizer,
        FrontendTarget frontend,
        string destinationDir,
        Action<LogLevel, LocalizedMessage> log)
    {
        if (frontend == FrontendTarget.None)
        {
            throw new ArgumentException("A run without a frontend has no platform folders.", nameof(frontend));
        }

        _platforms = platforms;
        _organizer = organizer;
        _frontend = frontend;
        _destinationDir = destinationDir;
        _log = log;
    }

    /// <summary>
    /// Folder of the platform of a file sorted with <paramref name="dat"/>, a single sanitized segment, in the case of an
    /// existing folder of the destination root; <c>null</c> for a MAME software list.
    /// </summary>
    public string? FolderOf(DatPlacement dat, ParsedRomName parsed)
    {
        string folder;
        if (_platforms.FindFolder(dat.Name, parsed.Convention, _frontend, parsed) is { } known)
        {
            folder = known.Name;
        }
        else
        {
            var platform = _platforms.FindPlatform(dat.Name, parsed.Convention);
            if (parsed.Convention == NamingConvention.MameSoftwareList)
            {
                ReportOnce($"unknown|{dat.Name}", () => LocalizedMessage.Create(
                    "log.frontend_software_list", ("dat", dat.Name), ("frontend", _frontend.DisplayName())));
                return null;
            }

            folder = _organizer.SanitizePathSegment(dat.Name.Trim());
            ReportOnce($"unknown|{dat.Name}", () => platform is null
                ? LocalizedMessage.Create("log.frontend_platform_unknown",
                    ("dat", dat.Name), ("frontend", _frontend.DisplayName()), ("folder", folder))
                : LocalizedMessage.Create("log.frontend_platform_missing",
                    ("dat", dat.Name), ("frontend", _frontend.DisplayName()), ("folder", folder)));
        }

        return InDestinationCase(_organizer.SanitizePathSegment(folder));
    }

    /// <summary>
    /// Notes a kept file of <paramref name="dat"/> written to <paramref name="target"/> (or that would be, in a
    /// simulation): its DAT for the folder of its platform (<see cref="ReportSharedFolders"/>), and a warning, once per
    /// DAT and extension, when the target is an archive (<c>.zip</c>, <c>.7z</c>, <c>.rar</c>) the frontend does not
    /// read in that folder. An archive the table does not know about, or a DAT without a known platform, gives no warning.
    /// </summary>
    public void NoteWritten(DatPlacement dat, ParsedRomName parsed, string target)
    {
        if (dat.PlatformFolder is { } folder)
        {
            lock (_lock)
            {
                if (!_datsByFolder.TryGetValue(folder, out var dats))
                {
                    _datsByFolder[folder] = dats = [];
                }

                if (!dats.Contains(dat.Name, StringComparer.OrdinalIgnoreCase))
                {
                    dats.Add(dat.Name);
                }
            }
        }

        var extension = Path.GetExtension(target);
        if (!ArchiveExtensions.Contains(extension)
            || _platforms.FindFolder(dat.Name, parsed.Convention, _frontend, parsed) is not { } known
            || known.AcceptsArchive(extension) != false)
        {
            return;
        }

        ReportOnce($"archive|{dat.Name}|{known.Name}|{extension}", () => LocalizedMessage.Create(
            "log.frontend_archive_unsupported",
            ("frontend", _frontend.DisplayName()),
            ("extension", extension.ToLowerInvariant()),
            ("folder", known.Name),
            ("dat", dat.Name)));
    }

    /// <summary>
    /// Says, once per folder, which folders of platforms received the files of several DATs: each DAT keeps its own 1G1R,
    /// so that a game two of them know may be written twice, its names told apart by the collision mode.
    /// </summary>
    public void ReportSharedFolders()
    {
        List<(string Folder, List<string> Dats)> shared;
        lock (_lock)
        {
            // In the order of the names, whatever the order the files were sorted in
            shared = [.. _datsByFolder
                .Where(pair => pair.Value.Count > 1)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => (pair.Key, pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToList()))];
        }

        foreach (var (folder, dats) in shared)
        {
            ReportOnce($"shared|{folder}", () => LocalizedMessage.Create(
                "log.frontend_platform_shared",
                ("frontend", _frontend.DisplayName()),
                ("folder", folder),
                ("count", dats.Count),
                ("dats", string.Join(", ", dats))), LogLevel.Info);
        }
    }

    /// <summary>
    /// <paramref name="folder"/> as the destination root already writes it when a folder there only differs by case, and
    /// as this run first wrote it otherwise: a destination prepared on a disk that tells case apart keeps one folder.
    /// </summary>
    private string InDestinationCase(string folder)
    {
        lock (_lock)
        {
            _folders ??= ListRootFolders();
            if (_folders.TryGetValue(folder, out var existing))
            {
                return existing;
            }

            _folders[folder] = folder;
            return folder;
        }
    }

    private Dictionary<string, string> ListRootFolders()
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_destinationDir))
            {
                // The quarantine and the marker never receive a sorted file (BuildTargetPath guards them anyway)
                var name = Path.GetFileName(directory);
                if (!name.Equals(DestinationGuard.QuarantineDirectoryName, StringComparison.OrdinalIgnoreCase)
                    && !name.Equals(DestinationGuard.MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    folders.TryAdd(name, name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A destination not created yet, or unreadable: the folders of the table, as they are
        }

        return folders;
    }

    private void ReportOnce(string key, Func<LocalizedMessage> message, LogLevel level = LogLevel.Warn)
    {
        bool first;
        lock (_lock)
        {
            first = _reported.Add(key);
        }

        if (first)
        {
            _log(level, message());
        }
    }
}
