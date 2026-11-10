using System.Globalization;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Writes the CRC32 of its origin in the name of an IPS, replaces it, or removes it to detach the patch from its ROM
/// (<see cref="IPatchNamingService"/>). Every check comes before the renaming, which never overwrites anything
/// (<see cref="File.Move(string, string, bool)"/> without overwrite).
/// </summary>
public sealed class PatchNamingService : IPatchNamingService
{
    private readonly IPatchService _patchService;

    public PatchNamingService(IPatchService patchService)
    {
        _patchService = patchService;
    }

    public string NameWithCrc(string fileName, uint crc)
    {
        var name = Path.GetFileName(fileName);
        var baseName = PatchMatcher.BaseNameWithoutCrc(Path.GetFileNameWithoutExtension(name)).TrimEnd();
        var crcText = crc.ToString("x8", CultureInfo.InvariantCulture);
        return baseName.Length == 0
            ? $"[{crcText}]{Path.GetExtension(name)}"
            : $"{baseName} [{crcText}]{Path.GetExtension(name)}";
    }

    public string? NameWithoutCrc(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var baseName = PatchMatcher.BaseNameWithoutCrc(Path.GetFileNameWithoutExtension(name));
        return baseName.Length == 0 ? null : baseName + Path.GetExtension(name);
    }

    public string? NewName(PatchRename rename) => rename.Crc32 is { } crc
        ? NameWithCrc(rename.Patch.DisplayName, crc)
        : NameWithoutCrc(rename.Patch.DisplayName);

    public async Task<IReadOnlyList<PatchRenameResult>> RenameAsync(
        string patchDir,
        IReadOnlyList<PatchRename> renames,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PatchRenameResult>(renames.Count);
        foreach (var rename in renames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RenameOneAsync(patchDir, rename, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            log(
                result.IsRenamed ? LogLevel.Info : LogLevel.Warn,
                result.IsRenamed
                    ? LocalizedMessage.Create(
                        rename.IsDetach ? "log.patch_detached" : "log.patch_renamed",
                        ("old", rename.Patch.DisplayName),
                        ("new", Path.GetFileName(result.NewPath!)))
                    : LocalizedMessage.Create("log.patch_rename_refused", ("name", rename.Patch.DisplayName), ("reason", result.Refusal!)));
        }

        return results;
    }

    private async Task<PatchRenameResult> RenameOneAsync(string patchDir, PatchRename rename, CancellationToken cancellationToken)
    {
        PatchRenameResult Refused(string key, params (string Name, object? Value)[] args) =>
            new(rename, null, LocalizedMessage.Create(key, args));

        var patch = rename.Patch;
        if (await CheckPatchFileAsync(patchDir, patch, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return new PatchRenameResult(rename, null, refusal);
        }

        // A patch is detached from the ROM its name gives; a CRC32 its name gives is replaced by the one chosen
        if (rename.IsDetach && !PatchMatcher.NameGivesCrc(patch.DisplayName))
        {
            return Refused("reason.patch_rename_no_rom");
        }

        var path = patch.SourcePath;
        var currentName = Path.GetFileName(path);
        var newName = rename.Crc32 is { } crc ? NameWithCrc(currentName, crc) : NameWithoutCrc(currentName);
        if (newName is null)
        {
            // Nothing but a CRC32 made the name: no name would be left
            return Refused("reason.patch_rename_nothing_left");
        }

        // The same CRC32 written again (whatever its case): the name already designates that ROM
        if (string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
        {
            return Refused("reason.patch_rename_unchanged");
        }

        if (rename.IsDetach && PatchMatcher.NameGivesCrc(newName))
        {
            return Refused("reason.patch_rename_unsafe", ("name", newName));
        }

        var newPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, newName);
        return MoveWithoutOverwriting(path, newName) is { } moveRefusal
            ? new PatchRenameResult(rename, null, moveRefusal)
            : new PatchRenameResult(rename, newPath, null);
    }

    public async Task<PatchUndoResult> UndoAsync(
        string patchDir,
        PatchRenameResult renaming,
        Action<LogLevel, LocalizedMessage> log,
        CancellationToken cancellationToken = default)
    {
        if (renaming.NewPath is not { } renamedPath)
        {
            throw new ArgumentException("Only a renaming done can be undone.", nameof(renaming));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = renaming.Request.Patch.SourcePath;
        var refusal = await UndoOneAsync(patchDir, renaming.Request.Patch, renamedPath, cancellationToken).ConfigureAwait(false);
        var result = new PatchUndoResult(renaming, refusal);
        log(
            result.IsUndone ? LogLevel.Info : LogLevel.Warn,
            result.IsUndone
                ? LocalizedMessage.Create("log.patch_rename_undone", ("new", Path.GetFileName(renamedPath)), ("old", Path.GetFileName(originalPath)))
                : LocalizedMessage.Create("log.patch_undo_refused", ("name", Path.GetFileName(renamedPath)), ("reason", refusal!)));
        return result;
    }

    private async Task<LocalizedMessage?> UndoOneAsync(string patchDir, PatchFile original, string renamedPath, CancellationToken cancellationToken)
    {
        if (original.ArchiveMember is not null)
        {
            return LocalizedMessage.Create("reason.patch_rename_in_archive");
        }

        // The patch as it is now, under its new name: checked as a renaming checks it
        if (await CheckPatchFileAsync(patchDir, new PatchFile(renamedPath), cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        // A renaming never leaves its folder: the old name is taken back in the folder the patch is in
        var oldName = Path.GetFileName(original.SourcePath);
        var folder = Path.GetDirectoryName(renamedPath) ?? string.Empty;
        if (!string.Equals(
                Path.GetFullPath(Path.Combine(folder, oldName)),
                Path.GetFullPath(original.SourcePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return LocalizedMessage.Create("reason.patch_rename_unsafe", ("name", oldName));
        }

        return MoveWithoutOverwriting(renamedPath, oldName);
    }

    /// <summary>
    /// Why a patch of the disk may not be renamed, or <see langword="null"/>: in an archive, outside the patch folder,
    /// missing, a link, or no longer an IPS nor an IPS32.
    /// </summary>
    private async Task<LocalizedMessage?> CheckPatchFileAsync(string patchDir, PatchFile patch, CancellationToken cancellationToken)
    {
        if (patch.ArchiveMember is not null)
        {
            // An archive is never modified: the patch has to be taken out of it first
            return LocalizedMessage.Create("reason.patch_rename_in_archive");
        }

        var path = patch.SourcePath;
        if (string.IsNullOrWhiteSpace(patchDir) || !PathOrganizerService.IsInsideDirectory(path, patchDir))
        {
            return LocalizedMessage.Create("reason.patch_rename_outside");
        }

        if (!File.Exists(path))
        {
            return LocalizedMessage.Create("reason.patch_rename_missing");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            return LocalizedMessage.Create("reason.patch_rename_link");
        }

        try
        {
            if (await ReadFormatAsync(path, cancellationToken).ConfigureAwait(false) is not (PatchFormat.Ips or PatchFormat.Ips32))
            {
                return LocalizedMessage.Create("reason.patch_rename_not_ips");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalizedMessage.Create("reason.patch_rename_failed", ("error", LocalizedMessage.Verbatim(ex.Message)));
        }

        return null;
    }

    /// <summary>
    /// Gives a file another name in its folder, never overwriting anything: the name must be safe and free. Why it was
    /// left as it was, or <see langword="null"/> once renamed.
    /// </summary>
    private static LocalizedMessage? MoveWithoutOverwriting(string path, string newName)
    {
        var newPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, newName);
        if (!PathOrganizerService.IsSafeRelativeName(newName) || Path.GetFileName(newPath) != newName
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return LocalizedMessage.Create("reason.patch_rename_unsafe", ("name", newName));
        }

        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            return LocalizedMessage.Create("reason.patch_rename_taken", ("name", newName));
        }

        try
        {
            File.Move(path, newPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalizedMessage.Create("reason.patch_rename_failed", ("error", LocalizedMessage.Verbatim(ex.Message)));
        }

        return null;
    }

    /// <summary>The format of a patch on the disk, from its signature.</summary>
    private async Task<PatchFormat?> ReadFormatAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var start = new byte[PatchService.SignatureLength];
        var read = await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return _patchService.DetectFormat(path, start.AsSpan(0, read));
    }
}
