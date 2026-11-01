using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RomSieve.Core.Abstractions;

namespace RomSieve.Core.Services;

/// <summary>
/// Implementation of the service for analyzing and linking optical descriptors
/// (.cue, .gdi, .ccd) and their associated companion tracks/files.
/// </summary>
public sealed class OpticalCompanionService : IOpticalCompanionService
{
    private static readonly HashSet<string> DescriptorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cue",
        ".gdi",
        ".ccd"
    };

    private static readonly HashSet<string> AudioDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin",
        ".raw",
        ".wav",
        ".iso",
        ".img",
        ".sub"
    };

    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly char[] WhitespaceSeparators = [' ', '\t'];

    public bool IsDescriptor(string pathOrFilename) => IsDescriptorFile(pathOrFilename);

    /// <summary>Whether the file is an optical descriptor (<c>.cue</c>, <c>.gdi</c>, <c>.ccd</c>...), by its extension.</summary>
    public static bool IsDescriptorFile(string pathOrFilename)
    {
        if (string.IsNullOrWhiteSpace(pathOrFilename))
        {
            return false;
        }

        var ext = Path.GetExtension(pathOrFilename);
        return DescriptorExtensions.Contains(ext);
    }

    public IReadOnlyList<string> ParseReferencedFiles(string descriptorPath)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath) || !File.Exists(descriptorPath))
        {
            return [];
        }

        try
        {
            var fileInfo = new FileInfo(descriptorPath);
            // Descriptor files are small text files (rarely > 1 MB)
            if (fileInfo.Length > 2 * 1024 * 1024)
            {
                return [];
            }

            var ext = Path.GetExtension(descriptorPath);
            if (string.Equals(ext, ".ccd", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCloneCdReferencedFiles(descriptorPath);
            }

            var content = File.ReadAllText(descriptorPath, Encoding.UTF8);
            return ParseReferencedFilesFromContent(content, ext);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> ParseReferencedFilesFromContent(string content, string descriptorExtension)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var ext = descriptorExtension.StartsWith('.') ? descriptorExtension : "." + descriptorExtension;
        if (string.Equals(ext, ".cue", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCueSheetContent(content);
        }

        if (string.Equals(ext, ".gdi", StringComparison.OrdinalIgnoreCase))
        {
            return ParseGdiContent(content);
        }

        return [];
    }

    public IReadOnlyList<string> ResolveCompanionFilesOnDisk(string descriptorPath)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath) || !File.Exists(descriptorPath))
        {
            return [];
        }

        var dir = Path.GetDirectoryName(descriptorPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return [];
        }

        var referenced = ParseReferencedFiles(descriptorPath);
        var resolved = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Map of existing files in directory for fast, case-insensitive lookup
        var dirFiles = Directory.EnumerateFiles(dir).ToList();
        var dirFilesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in dirFiles)
        {
            var fn = Path.GetFileName(f);
            if (!dirFilesByName.ContainsKey(fn))
            {
                dirFilesByName[fn] = f;
            }
        }

        foreach (var refItem in referenced)
        {
            var fileName = Path.GetFileName(refItem.Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName) || !seenNames.Add(fileName))
            {
                continue;
            }

            // 1. Case-insensitive search in directory to retrieve actual case on disk
            if (dirFilesByName.TryGetValue(fileName, out var existingFile) && File.Exists(existingFile))
            {
                resolved.Add(existingFile);
                continue;
            }

            // 2. Direct path (fallback if not enumerated)
            var direct = Path.Combine(dir, fileName);
            if (File.Exists(direct))
            {
                resolved.Add(direct);
            }
        }

        // Fallback heuristic for multi-track Redump games when .cue is damaged or unspecified
        if (resolved.Count == 0 && string.Equals(Path.GetExtension(descriptorPath), ".cue", StringComparison.OrdinalIgnoreCase))
        {
            var cueBaseName = Path.GetFileNameWithoutExtension(descriptorPath);
            var trackPattern = cueBaseName + " (Track ";
            foreach (var f in dirFiles)
            {
                var name = Path.GetFileName(f);
                var fExt = Path.GetExtension(f);
                if (AudioDataExtensions.Contains(fExt) &&
                    name.StartsWith(trackPattern, StringComparison.OrdinalIgnoreCase) &&
                    seenNames.Add(name))
                {
                    resolved.Add(f);
                }
            }
        }

        return resolved;
    }

    public IReadOnlyList<string> ResolveCompanionArchiveMembers(
        string descriptorMemberName,
        IEnumerable<string> allArchiveMembers,
        Func<string, Stream> openEntryStream)
    {
        if (string.IsNullOrWhiteSpace(descriptorMemberName) || !IsDescriptor(descriptorMemberName))
        {
            return [];
        }

        // Several members may share a file name in different folders: a track is looked for next to its descriptor first
        var membersByFileName = allArchiveMembers
            .GroupBy(MemberFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var descriptorFolder = MemberFolder(descriptorMemberName);

        var ext = Path.GetExtension(descriptorMemberName);
        IReadOnlyList<string> referencedNames;

        if (string.Equals(ext, ".ccd", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(descriptorMemberName);
            referencedNames = [baseName + ".img", baseName + ".sub"];
        }
        else
        {
            try
            {
                using var stream = openEntryStream(descriptorMemberName);
                if (stream == Stream.Null || !stream.CanRead)
                {
                    return [];
                }

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var text = reader.ReadToEnd();
                referencedNames = ParseReferencedFilesFromContent(text, ext);
            }
            catch (Exception)
            {
                return [];
            }
        }

        var resolvedMembers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var refName in referencedNames)
        {
            var cleanName = Path.GetFileName(refName.Replace('\\', '/'));
            if (!string.IsNullOrEmpty(cleanName) && seen.Add(cleanName) && membersByFileName.TryGetValue(cleanName, out var candidates))
            {
                resolvedMembers.Add(
                    candidates.FirstOrDefault(c => string.Equals(MemberFolder(c), descriptorFolder, StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0]);
            }
        }

        return resolvedMembers;
    }

    // File name and folder of an archive member, whatever its separators
    private static string MemberFileName(string member) => Path.GetFileName(member.Replace('\\', '/'));

    private static string MemberFolder(string member)
    {
        var normalized = member.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    public string RewriteReferencedFiles(
        string content,
        string descriptorExtension,
        IReadOnlyDictionary<string, string> renamedFiles)
    {
        if (string.IsNullOrEmpty(content) || renamedFiles.Count == 0)
        {
            return content;
        }

        var ext = descriptorExtension.StartsWith('.') ? descriptorExtension : "." + descriptorExtension;
        var isCue = string.Equals(ext, ".cue", StringComparison.OrdinalIgnoreCase);
        var isGdi = string.Equals(ext, ".gdi", StringComparison.OrdinalIgnoreCase);
        if (!isCue && !isGdi)
        {
            return content;
        }

        // Splitting on '\n' only keeps any '\r' attached to its line, preserving the original line endings
        var lines = content.Split('\n');
        var isFirstNonEmptyLine = true;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            // The first line of a GDI file is the track count
            var skip = isGdi && isFirstNonEmptyLine;
            isFirstNonEmptyLine = false;
            if (skip)
            {
                continue;
            }

            var reference = isCue ? ExtractCueFileReference(line) : ExtractGdiFileReference(line);
            if (string.IsNullOrEmpty(reference))
            {
                continue;
            }

            var normalized = reference.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(fileName) || !renamedFiles.TryGetValue(fileName, out var newFileName))
            {
                continue;
            }

            var newReference = reference[..^fileName.Length] + newFileName;
            var index = line.IndexOf(reference, StringComparison.Ordinal);
            if (index >= 0)
            {
                lines[i] = line[..index] + newReference + line[(index + reference.Length)..];
            }
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<string> ParseCueSheetContent(string content)
    {
        var results = new List<string>();
        foreach (var rawLine in content.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var filename = ExtractCueFileReference(rawLine);
            if (!string.IsNullOrEmpty(filename))
            {
                results.Add(filename);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts the file name of a <c>FILE</c> directive of a cue sheet line, or <c>null</c>.
    /// </summary>
    private static string? ExtractCueFileReference(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length < 5 || !line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(line[4]))
        {
            return null;
        }

        var remainder = line[4..].TrimStart();
        if (string.IsNullOrEmpty(remainder))
        {
            return null;
        }

        string filename;
        if (remainder.StartsWith('"'))
        {
            var nextQuote = remainder.IndexOf('"', 1);
            filename = nextQuote > 1 ? remainder[1..nextQuote] : remainder.Trim('"');
        }
        else if (remainder.StartsWith('\''))
        {
            var nextQuote = remainder.IndexOf('\'', 1);
            filename = nextQuote > 1 ? remainder[1..nextQuote] : remainder.Trim('\'');
        }
        else
        {
            // Unquoted format: FILE filename.bin BINARY
            var lastSpace = remainder.LastIndexOf(' ');
            filename = lastSpace > 0 ? remainder[..lastSpace].Trim() : remainder;
        }

        filename = filename.Trim();
        return filename.Length > 0 ? filename : null;
    }

    private static IReadOnlyList<string> ParseGdiContent(string content)
    {
        var results = new List<string>();
        var lines = content.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);

        // Line 1: number of tracks
        for (var i = 1; i < lines.Length; i++)
        {
            var filename = ExtractGdiFileReference(lines[i]);
            if (!string.IsNullOrEmpty(filename))
            {
                results.Add(filename);
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts the file name of a GDI track line, or <c>null</c>.
    /// </summary>
    private static string? ExtractGdiFileReference(string rawLine)
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        // Search for quoted string
        var quoteStart = line.IndexOf('"');
        if (quoteStart >= 0)
        {
            var quoteEnd = line.IndexOf('"', quoteStart + 1);
            if (quoteEnd > quoteStart)
            {
                return line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
            }
        }

        // Whitespace tokenization: track_num starting_lba type sector_size filename [offset]
        var tokens = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 5 ? tokens[4] : null;
    }

    private static IReadOnlyList<string> ParseCloneCdReferencedFiles(string ccdPath)
    {
        var baseWithoutExt = Path.GetFileNameWithoutExtension(ccdPath);
        return
        [
            baseWithoutExt + ".img",
            baseWithoutExt + ".sub"
        ];
    }
}

