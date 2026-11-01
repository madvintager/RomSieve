namespace RomSieve.Core.Models;

/// <summary>
/// State of a destination directory before a real sort.
/// </summary>
/// <param name="HasContent">Whether it holds anything besides the RomSieve marker and the quarantine of the purges.</param>
/// <param name="HasMarker">Whether it holds the marker written by RomSieve in the destinations of its real sorts.</param>
public sealed record DestinationState(bool HasContent, bool HasMarker)
{
    /// <summary>A destination with content that RomSieve did not mark: purging it deserves a stronger warning.</summary>
    public bool IsForeign => HasContent && !HasMarker;
}
