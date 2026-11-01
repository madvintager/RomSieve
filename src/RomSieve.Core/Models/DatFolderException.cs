using System.IO;

namespace RomSieve.Core.Models;

/// <summary>
/// A folder of DATs that cannot be read as the DATs of MAME software lists: empty, a DAT of another convention, two DATs
/// of the same list. <see cref="Reason"/> says why, translated where it is shown.
/// </summary>
public sealed class DatFolderException(LocalizedMessage reason) : IOException(reason.Key)
{
    public LocalizedMessage Reason { get; } = reason;
}
