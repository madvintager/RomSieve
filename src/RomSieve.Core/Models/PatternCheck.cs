namespace RomSieve.Core.Models;

/// <summary>
/// Problems of a directory pattern: variables the sort does not know (dropped from the path) and braces without
/// their pair (kept as text).
/// </summary>
public sealed record PatternCheck(IReadOnlyList<string> UnknownVariables, bool HasUnbalancedBraces)
{
    public bool IsValid => UnknownVariables.Count == 0 && !HasUnbalancedBraces;
}
