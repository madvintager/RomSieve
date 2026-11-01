using RomSieve.Core.Abstractions;

namespace RomSieve.App.ViewModels;

/// <summary>
/// Text and tooltip of a button of the "Refine" row of the MAME card, whose options open in a drop-down panel: the button
/// names the only restriction in force, or counts them, so that a filter still acting is not forgotten while its panel
/// is closed; the tooltip names them all.
/// </summary>
internal static class MameRefineSummary
{
    /// <summary>
    /// Text of the button: <paramref name="noneKey"/> without restriction, <paramref name="oneKey"/> (parameter
    /// <c>{restriction}</c>) with one, <paramref name="manyKey"/> (parameter <c>{count}</c>) with more.
    /// </summary>
    public static string Text(ILocalizationService localization, IReadOnlyList<string> restrictions, string noneKey, string oneKey, string manyKey) =>
        restrictions.Count switch
        {
            0 => localization.Tr(noneKey),
            1 => localization.Tr(oneKey, ("restriction", restrictions[0])),
            var count => localization.Tr(manyKey, ("count", count))
        };

    /// <summary>Tooltip of the button: what its panel does, then the restrictions in force.</summary>
    public static string Tooltip(ILocalizationService localization, string tooltip, IReadOnlyList<string> restrictions) =>
        restrictions.Count == 0
            ? tooltip
            : tooltip + Environment.NewLine + Environment.NewLine +
              localization.Tr("filters.mame_restrictions", ("names", string.Join(", ", restrictions)));
}
