using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RomSieve.Core.Abstractions;

namespace RomSieve.Core.Services;

/// <summary>
/// Writes a crash report per unexpected exception (<c>crash-&lt;date&gt;-&lt;id&gt;.log</c>), for the user to attach to a
/// bug report. The report is a technical document for the developers: it stays in English, whatever the language of
/// the interface.
/// </summary>
public sealed class CrashReportService(string crashDirectory) : ICrashReportService
{
    private const string FilePrefix = "crash-";
    private const string FileExtension = ".log";

    public string CrashDirectory { get; } = crashDirectory;

    /// <summary>Default report folder: <c>crashes/</c> next to the configuration file.</summary>
    public static string GetCrashDirectory(string configFilePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? string.Empty, "crashes");

    public string? WriteReport(Exception exception, string origin)
    {
        try
        {
            Directory.CreateDirectory(CrashDirectory);
            var now = DateTime.UtcNow;
            var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(CrashDirectory, $"{FilePrefix}{stamp}-{Guid.NewGuid().ToString("N")[..6]}{FileExtension}");

            // CreateNew: a report never replaces another one
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(BuildReport(exception, origin, now));
            return path;
        }
        catch (Exception)
        {
            // Nothing else to do: the caller tells the user that no report could be written
            return null;
        }
    }

    internal static string BuildReport(Exception exception, string origin, DateTime utcNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RomSieve crash report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Version: {ProductVersion.Current}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date (UTC): {utcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Origin: {origin}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Culture: {CultureInfo.CurrentCulture.Name} / {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine();

        // ToString gives the type, the message and the stack trace, inner exceptions included
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }
}
