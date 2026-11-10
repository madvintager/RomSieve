using System.Globalization;
using System.IO;
using RomSieve.Core.Abstractions;
using RomSieve.Core.Models;

namespace RomSieve.Core.Services;

/// <summary>
/// Writes the CRC32 of its origin in the name of an IPS (<see cref="IPatchNamingService"/>). Every check comes before the
/// renaming, which never overwrites anything (<see cref="File.Move(string, string, bool)"/> without overwrite).
/// </summary>
public sealed class PatchNamingService : IPatchNamingService
{
    private readonly IPatchService _patchService;
    private readonly IChecksumService _checksumService;
    private readonly ICrcCacheService _crcCache;

    public PatchNamingService(IPatchService patchService, IChecksumService checksumService, ICrcCacheService crcCache)
    {
        _patchService = patchService;
        _checksumService = checksumService;
        _crcCache = crcCache;
    }

    public async Task<IReadOnlyList<PatchOriginSuggestion>> GetOriginFormsAsync(
        ProcessItem origin,
        bool detectCopierHeaders,
        CancellationToken cancellationToken = default)
    {
        if (PatchOriginForms.IdentifiedForm(origin) is not { } identified)
        {
            return [];
        }

        var forms = new List<(long Offset, long Size, uint Crc32)> { identified };
        if (detectCopierHeaders
            && await PatchOriginForms.OtherFormAsync(origin, _ => true, _checksumService, _crcCache, cancellationToken).ConfigureAwait(false) is { } other)
        {
            forms.Add(other);
        }

        var largest = forms.Max(f => f.Size);
        return [.. forms
            .OrderBy(f => f.Offset)
            .Select(f => new PatchOriginSuggestion(origin, f.Crc32, f.Offset, forms.Count > 1 ? f.Size == largest : null))];
    }

    public string NameWithCrc(string fileName, uint crc)
    {
        var name = Path.GetFileName(fileName);
        var baseName = Path.GetFileNameWithoutExtension(name).TrimEnd();
        var crcText = crc.ToString("x8", CultureInfo.InvariantCulture);
        return baseName.Length == 0
            ? $"[{crcText}]{Path.GetExtension(name)}"
            : $"{baseName} [{crcText}]{Path.GetExtension(name)}";
    }

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
                    ? LocalizedMessage.Create("log.patch_renamed", ("old", rename.Patch.DisplayName), ("new", Path.GetFileName(result.NewPath!)))
                    : LocalizedMessage.Create("log.patch_rename_refused", ("name", rename.Patch.DisplayName), ("reason", result.Refusal!)));
        }

        return results;
    }

    private async Task<PatchRenameResult> RenameOneAsync(string patchDir, PatchRename rename, CancellationToken cancellationToken)
    {
        PatchRenameResult Refused(string key, params (string Name, object? Value)[] args) =>
            new(rename, null, LocalizedMessage.Create(key, args));

        var patch = rename.Patch;
        if (patch.ArchiveMember is not null)
        {
            // An archive is never modified: the patch has to be taken out of it first
            return Refused("reason.patch_rename_in_archive");
        }

        var path = patch.SourcePath;
        if (string.IsNullOrWhiteSpace(patchDir) || !PathOrganizerService.IsInsideDirectory(path, patchDir))
        {
            return Refused("reason.patch_rename_outside");
        }

        if (!File.Exists(path))
        {
            return Refused("reason.patch_rename_missing");
        }

        if (new FileInfo(path).LinkTarget is not null)
        {
            return Refused("reason.patch_rename_link");
        }

        if (PatchMatcher.NameGivesCrc(patch.DisplayName))
        {
            return Refused("reason.patch_rename_has_crc");
        }

        try
        {
            if (await ReadFormatAsync(path, cancellationToken).ConfigureAwait(false) is not (PatchFormat.Ips or PatchFormat.Ips32))
            {
                return Refused("reason.patch_rename_not_ips");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("reason.patch_rename_failed", ("error", LocalizedMessage.Verbatim(ex.Message)));
        }

        var newName = NameWithCrc(path, rename.Crc32);
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var newPath = Path.Combine(folder, newName);
        if (!PathOrganizerService.IsSafeRelativeName(newName) || Path.GetFileName(newPath) != newName
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Refused("reason.patch_rename_unsafe", ("name", newName));
        }

        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            return Refused("reason.patch_rename_taken", ("name", newName));
        }

        try
        {
            File.Move(path, newPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused("reason.patch_rename_failed", ("error", LocalizedMessage.Verbatim(ex.Message)));
        }

        return new PatchRenameResult(rename, newPath, null);
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
