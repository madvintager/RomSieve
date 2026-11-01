namespace RomSieve.Core.Models;

/// <summary>Kind of media stored in a CHD, from its metadata.</summary>
public enum ChdMediaKind
{
    Unknown,
    Cd,
    GdRom,
    Dvd,
    HardDisk
}

/// <summary>A track of a CD or GD-ROM CHD: number, type (<c>MODE1_RAW</c>, <c>AUDIO</c>...) and length in frames.</summary>
public sealed record ChdTrack(int Number, string Type, int Frames);

/// <summary>
/// What a CHD header and its metadata say, read without decompressing the data. <see cref="Sha1"/> covers the data
/// and the metadata: it is the SHA-1 of the CHD disks of MAME-style DATs.
/// </summary>
public sealed record ChdInfo(
    int Version,
    long LogicalBytes,
    string Sha1,
    string RawSha1,
    string? ParentSha1,
    ChdMediaKind MediaKind,
    IReadOnlyList<ChdTrack> Tracks)
{
    /// <summary>Whether the CHD only stores the differences from a parent CHD, needed to read it.</summary>
    public bool IsDifferential => ParentSha1 is not null;
}
