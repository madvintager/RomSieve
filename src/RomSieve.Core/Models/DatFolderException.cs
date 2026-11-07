using System.IO;

namespace RomSieve.Core.Models;

/// <summary>
/// A folder of DATs that cannot be sorted: empty, holding a DAT of MAME machines, mixing MAME software lists and other
/// DATs, two DATs of the same list or of the same name, every DAT left out. <see cref="Reason"/> says why, translated
/// where it is shown.
/// </summary>
public sealed class DatFolderException(LocalizedMessage reason) : IOException(reason.Key)
{
    public LocalizedMessage Reason { get; } = reason;
}
