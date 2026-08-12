using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// The two read-only administrative cleaning queries (Fase 6, Incremento 1)
/// — mirrors <c>Reservations.Application.Reservations.IReservationReader</c>'s
/// shape (its update-snapshot/xmin members are not needed this increment —
/// concurrency conflicts are translated directly from
/// <c>DbUpdateConcurrencyException</c> at the executor, mirroring
/// <c>CancelReservationExecutor</c>'s own simpler path, never
/// <c>UpdateReservationExecutor</c>'s snapshot-comparison one, since no
/// Housekeeping command diffs multiple optional fields the way
/// <c>PATCH /reservations/{id}</c> does).
/// </summary>
public interface ICleaningReader
{
    /// <summary>
    /// <paramref name="pageSize"/> is defensively clamped to the fixed
    /// maximum (100) by the implementation itself — never trusts the caller
    /// alone. Ordered deterministically by <c>createdAtUtc</c> then
    /// <c>id</c>.
    /// </summary>
    Task<PagedResult<CleaningSummaryResult>> ListAsync(
        string? status, Guid? propertyId, Guid? assignedHousekeeperUserId,
        int page, int pageSize, CancellationToken cancellationToken);

    Task<CleaningResult?> GetByIdAsync(Guid cleaningId, CancellationToken cancellationToken);
}
