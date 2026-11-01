using System.IO;
using System.Text;

namespace RomSieve.Core.Services;

/// <summary>
/// Writes a file in one step: the content goes to a temporary file of the same folder, flushed to the disk, which then
/// replaces the target. A crash, a power cut or a full disk while writing leaves the previous file intact, never a
/// truncated one (a truncated <c>config.json</c> would bring every setting back to its default value).
/// </summary>
internal static class AtomicFile
{
    /// <summary>Attempts to replace the target: on Windows, a process reading it at that moment makes the replacement fail.</summary>
    internal const int ReplaceAttempts = 5;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    private const string TempExtension = ".tmp";

    public static void WriteAllText(string path, string contents, Encoding encoding) =>
        Write(path, stream => WriteText(stream, contents, encoding));

    public static Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteAsync(path, (stream, ct) => WriteTextAsync(stream, contents, encoding, ct), cancellationToken);

    /// <summary>Writes the file with <paramref name="write"/>, which receives the stream of the temporary file.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        path = ResolveTarget(path);
        DeleteStaleTempFiles(path);
        var tempPath = CreateTempPath(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            KeepUnixFileMode(path, tempPath);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceAttempts)
                {
                    Thread.Sleep(ReplaceRetryDelay * attempt);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <inheritdoc cref="Write"/>
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        path = ResolveTarget(path);
        DeleteStaleTempFiles(path);
        var tempPath = CreateTempPath(path);
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // No asynchronous form: the data must be on the disk before the file is replaced
                stream.Flush(flushToDisk: true);
            }

            KeepUnixFileMode(path, tempPath);

            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceAttempts)
                {
                    await Task.Delay(ReplaceRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Temporary file next to the target, so that the replacement stays on the same volume (a rename, not a copy).
    /// Unique: two processes (the interface and the command line) may save the same file at once.
    /// </summary>
    internal static string CreateTempPath(string path) => $"{path}.{Guid.NewGuid():N}{TempExtension}";

    private const int TempIdLength = 32;

    /// <summary>
    /// Removes the temporary files of <paramref name="path"/> that a process stopped between the write and the
    /// replacement left behind: only the names <see cref="CreateTempPath"/> gives, and only once older than
    /// <see cref="DestinationGuard.StaleTemporaryFileAge"/>, so that a write in progress elsewhere is never touched.
    /// </summary>
    private static void DeleteStaleTempFiles(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var prefix = Path.GetFileName(path) + ".";
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*" + TempExtension))
            {
                if (IsTempNameOf(Path.GetFileName(file), prefix)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= DestinationGuard.StaleTemporaryFileAge)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place: it is never read
        }
    }

    private static bool IsTempNameOf(string fileName, string prefix) =>
        fileName.Length == prefix.Length + TempIdLength + TempExtension.Length
        && fileName.StartsWith(prefix, StringComparison.Ordinal)
        && fileName.EndsWith(TempExtension, StringComparison.Ordinal)
        && fileName.AsSpan(prefix.Length, TempIdLength).ToString().All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// A link (a <c>config.json</c> kept in a folder of dotfiles, for example) is written through: the replacement
    /// would otherwise put a plain file in its place.
    /// </summary>
    private static string ResolveTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Broken or looping link: written in place, as before
            return path;
        }
    }

    /// <summary>The new file keeps the permissions of the one it replaces (a configuration readable by its owner only).</summary>
    private static void KeepUnixFileMode(string path, string tempPath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The content matters more than its permissions: written with the default ones
        }
    }

    private static void WriteText(Stream stream, string contents, Encoding encoding)
    {
        stream.Write(encoding.GetPreamble());
        stream.Write(encoding.GetBytes(contents));
    }

    private static async Task WriteTextAsync(Stream stream, string contents, Encoding encoding, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(encoding.GetPreamble(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(encoding.GetBytes(contents), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: it never replaces the target
        }
    }
}
