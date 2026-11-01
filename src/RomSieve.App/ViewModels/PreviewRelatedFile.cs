namespace RomSieve.App.ViewModels;

/// <summary>
/// File transferred with the file of a row of the preview table (a track of a descriptor, a CHD of a MAME set, another
/// file of the same DAT game), shown in the detail panel of the row.
/// </summary>
/// <param name="Name">Name of the file in the source (or member of its archive).</param>
/// <param name="SizeText">Formatted size, empty when unknown.</param>
/// <param name="TargetText">
/// Target of the file, relative to the destination when it lies inside; it shows the new name of a track renamed to
/// avoid a collision. Empty before a target is computed.
/// </param>
/// <param name="ChdText">Version, media and tracks of a CHD; empty for any other file.</param>
public sealed record PreviewRelatedFile(string Name, string SizeText, string TargetText, string ChdText)
{
    public bool HasSize => SizeText.Length > 0;

    /// <summary>Size written after the name (<c> (2.00 KB)</c>), empty when unknown.</summary>
    public string SizeSuffix => HasSize ? $" ({SizeText})" : string.Empty;

    public bool HasTarget => TargetText.Length > 0;

    public bool HasChdText => ChdText.Length > 0;
}
