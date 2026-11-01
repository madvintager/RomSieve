namespace RomSieve.Core.Models;

/// <summary>
/// What a whole MAME DAT says of its mechanical machines, so that the filters page can warn when the options keep the
/// mechanical machines but the driver statuses kept leave none of them (in MAME 0.289, all of them are preliminary).
/// </summary>
public sealed class MameDatSummary
{
    private readonly IReadOnlyList<(MameDriverStatus Status, MameDriverStatus Emulation)> _mechanicalDrivers;

    private MameDatSummary(IReadOnlyList<(MameDriverStatus Status, MameDriverStatus Emulation)> mechanicalDrivers)
    {
        _mechanicalDrivers = mechanicalDrivers;
    }

    /// <summary>
    /// Summary of <paramref name="catalog"/>: the driver statuses of its mechanical machines that a collection could hold
    /// (with a ROM or a disk to dump), or <c>null</c> when it has none (a DAT of another convention, a SabreTools DAT).
    /// </summary>
    public static MameDatSummary? FromCatalog(DatCatalog catalog)
    {
        var drivers = catalog.Games
            .Where(g => g.IsMechanical && g.Roms.Any(r => r.Status != DatRomStatus.NoDump))
            .Select(g => (g.DriverStatus, g.EmulationStatus))
            .Distinct()
            .ToList();
        return drivers.Count == 0 ? null : new MameDatSummary(drivers);
    }

    /// <summary>Whether the statuses kept (and the playable machines, if kept) leave no mechanical machine of the DAT.</summary>
    public bool KeepsNoMechanicalMachine(MameDriverStatuses statuses, bool keepPlayable) =>
        !_mechanicalDrivers.Any(d => statuses.Keeps(keepPlayable, d.Status, d.Emulation));
}
