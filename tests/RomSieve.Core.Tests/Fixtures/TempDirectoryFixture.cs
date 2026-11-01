using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Services;

namespace RomSieve.Core.Tests.Fixtures;

/// <summary>
/// Creates an isolated temporary directory on disk and cleans it up automatically after each test execution.
/// </summary>
public sealed class TempDirectoryFixture : IDisposable
{
    public string Path { get; }

    public TempDirectoryFixture()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "romsieve_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateSubdirectory(string relativePath)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public string GetFilePath(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath);

    public string WriteText(string relativePath, string content, Encoding? encoding = null)
    {
        var fullPath = GetFilePath(relativePath);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(fullPath, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var fullPath = GetFilePath(relativePath);
        var dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    /// <summary>What gives back the right to list the folders made unreadable, before they are deleted.</summary>
    private readonly List<Action> _restoreListing = [];

    /// <summary>
    /// Creates a folder that the current user cannot list, as a folder of the system or of another user; the right is
    /// given back when the fixture is disposed. <see langword="false"/> where it cannot be denied: the superuser of Linux
    /// reads every folder.
    /// </summary>
    public bool TryCreateUnreadableDirectory(string relativePath)
    {
        var path = CreateSubdirectory(relativePath);
        _restoreListing.Add(OperatingSystem.IsWindows() ? DenyWindowsListing(path) : DenyUnixListing(path));
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Action DenyWindowsListing(string path)
    {
        var info = new DirectoryInfo(path);
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current user has no security identifier.");
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return () =>
        {
            var restored = info.GetAccessControl();
            restored.RemoveAccessRule(rule);
            info.SetAccessControl(restored);
        };
    }

    [UnsupportedOSPlatform("windows")]
    private static Action DenyUnixListing(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return () => File.SetUnixFileMode(path, mode);
    }

    public void Dispose()
    {
        foreach (var restore in _restoreListing)
        {
            try
            {
                restore();
            }
            catch
            {
                // The folder is then left behind
            }
        }

        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors on locked temporary files
        }
    }
}

/// <summary>
/// Factory of isolated <c>RomSieve.Core</c> service instances for unit and integration tests.
/// </summary>
public sealed class CoreTestServices
{
    private static readonly Lazy<string> IsolatedCrcCacheDirectory = new(() =>
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "romsieve_tests_crc_" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        };
        return directory;
    });

    /// <summary>
    /// CRC cache file shared by the tests of the running process and deleted when it exits: the tests must neither
    /// read nor fill the cache of the user (<see cref="CrcCacheService.GetDefaultCrcCachePath"/>).
    /// </summary>
    public static string IsolatedCrcCachePath => System.IO.Path.Combine(IsolatedCrcCacheDirectory.Value, "crc_cache.json");

    public LocalizationService I18n { get; }
    public TosecParser TosecParser { get; }
    public NoIntroRedumpParser NoIntroParser { get; }
    public RomFilterService FilterService { get; }
    public PathOrganizerService Organizer { get; }
    public ChecksumService ChecksumService { get; }
    public DatService DatService { get; }
    public OpticalCompanionService OpticalCompanionService { get; }
    public MameDescriptionParser MameParser { get; }
    public CrcCacheService CrcCache { get; }
    public SortProcessorService Processor { get; }

    public CoreTestServices(string locale = "fr")
    {
        I18n = new LocalizationService();
        I18n.SetLocale(locale);
        TosecParser = new TosecParser();
        NoIntroParser = new NoIntroRedumpParser(TosecParser);
        FilterService = RomFilterService.CreateDefault();
        Organizer = new PathOrganizerService(TosecParser);
        ChecksumService = new ChecksumService();
        CrcCache = new CrcCacheService(IsolatedCrcCachePath);
        DatService = new DatService(ChecksumService);
        OpticalCompanionService = new OpticalCompanionService();
        MameParser = new MameDescriptionParser();
        Processor = new SortProcessorService(
            ChecksumService,
            DatService,
            NoIntroParser,
            FilterService,
            Organizer,
            I18n,
            OpticalCompanionService,
            MameParser,
            CrcCache);
    }
}
