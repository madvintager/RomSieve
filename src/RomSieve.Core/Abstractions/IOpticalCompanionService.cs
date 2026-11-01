using System;
using System.Collections.Generic;
using System.IO;

namespace RomSieve.Core.Abstractions;

/// <summary>
/// Service contract for detecting, parsing, and linking optical descriptors
/// (.cue, .gdi, .ccd) and their associated companion files / tracks (.bin, .raw, .iso, .img, .sub).
/// </summary>
public interface IOpticalCompanionService
{
    /// <summary>
    /// Indicates whether the file extension corresponds to an optical descriptor (.cue, .gdi, .ccd).
    /// </summary>
    bool IsDescriptor(string pathOrFilename);

    /// <summary>
    /// Parses the descriptor file on disk and extracts the list of referenced files.
    /// </summary>
    IReadOnlyList<string> ParseReferencedFiles(string descriptorPath);

    /// <summary>
    /// Parses the text content of a descriptor and extracts the list of referenced files.
    /// </summary>
    IReadOnlyList<string> ParseReferencedFilesFromContent(string content, string descriptorExtension);

    /// <summary>
    /// Rewrites the file references of a descriptor (.cue, .gdi) whose file name appears in
    /// <paramref name="renamedFiles"/> (old file name → new file name, case-insensitive keys expected).
    /// Directory prefixes, quoting, and line endings are preserved. Other formats are returned unchanged.
    /// </summary>
    string RewriteReferencedFiles(string content, string descriptorExtension, IReadOnlyDictionary<string, string> renamedFiles);

    /// <summary>
    /// Resolves on disk all existing companion files associated with the specified descriptor.
    /// </summary>
    IReadOnlyList<string> ResolveCompanionFilesOnDisk(string descriptorPath);

    /// <summary>
    /// Resolves among members of the same ZIP archive the companion files associated with the descriptor.
    /// </summary>
    IReadOnlyList<string> ResolveCompanionArchiveMembers(
        string descriptorMemberName,
        IEnumerable<string> allArchiveMembers,
        Func<string, Stream> openEntryStream);
}

