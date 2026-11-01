using RomSieve.Core.Models;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Change of a preview row since the previous simulation of the same source. Its name is also the theme tone
/// of the row marker (<c>RowChange{Change}MarkerBrush</c>).
/// </summary>
public enum PreviewRowChange
{
    /// <summary>Same status, target and reason as in the previous simulation, or no previous simulation to compare with.</summary>
    None,

    /// <summary>File absent from the previous simulation.</summary>
    Added,

    /// <summary>Status, target or reason different from the previous simulation.</summary>
    Modified
}

/// <summary>
/// What a differential simulation compares for a row: its status, its target and the reason of its status (structured,
/// compared by key and arguments, so that a change of language is no change).
/// </summary>
public readonly record struct PreviewRowState(FileStatus Status, string? Target, LocalizedMessage? Reason)
{
    public bool Equals(PreviewRowState other) =>
        Status == other.Status
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && Equals(Reason, other.Reason);

    public override int GetHashCode() => HashCode.Combine(Status, Target);
}
