using System.Globalization;
using System.Text;

namespace RomSieve.Core.Services;

/// <summary>
/// Log of one run of the interface (a simulation, a sort, an undo) written to a file, <c>run-&lt;date&gt;-&lt;id&gt;.log</c>
/// in <c>logs/</c> next to the configuration: every line, where the log panel only keeps the last ones, for the user to
/// read again or to attach to a bug report. Lines are written in the language of the interface, each flushed at once so
/// that a crash keeps them. Writing it never stops a run: a file that cannot be written is given up.
/// </summary>
public sealed class RunLogFile : IDisposable
{
    private const string FilePrefix = "run-";
    private const string FileExtension = ".log";

    /// <summary>Run logs kept, the one of the run that begins included; older ones are deleted when a run begins.</summary>
    public const int KeptLogCount = 20;

    private readonly Lock _lock = new();
    private StreamWriter? _writer;

    private RunLogFile(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>Path of the file.</summary>
    public string Path { get; }

    /// <summary>Default folder of the run logs: <c>logs/</c> next to the configuration file.</summary>
    public static string GetLogDirectory(string configFilePath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(configFilePath)) ?? string.Empty, "logs");

    /// <summary>
    /// Starts the log of a run in <paramref name="directory"/>, headed by <paramref name="title"/> (what runs) and the
    /// version, after deleting the logs beyond the <see cref="KeptLogCount"/> most recent; <c>null</c> when the file
    /// cannot be created.
    /// </summary>
    public static RunLogFile? Create(string directory, string title)
    {
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOldLogs(directory);
            var now = DateTime.Now;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var path = System.IO.Path.Combine(directory, $"{FilePrefix}{stamp}-{Guid.NewGuid().ToString("N")[..6]}{FileExtension}");

            // CreateNew: a log never replaces another one
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RomSieve {ProductVersion.Current} - {title} - {now:yyyy-MM-dd HH:mm:ss}"));
            return new RunLogFile(path, writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Appends a line, preceded by the local time; after a write error, the file is given up.</summary>
    public void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} {line}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                CloseWriter();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseWriter();
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lines written so far stay
        }

        _writer = null;
    }

    // File names start with a sortable UTC timestamp
    private static void DeleteOldLogs(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileExtension)
                     .OrderByDescending(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal)
                     .Skip(KeptLogCount - 1)
                     .ToList())
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next run
            }
        }
    }
}
