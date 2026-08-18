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

    /// <summary>
    /// Self-service "Minhas Faxinas" listing (Fase 6, Incremento 2A) —
    /// <paramref name="housekeeperUserId"/> is a mandatory filter, always
    /// taken from the caller's own authenticated identity, never
    /// client-supplied (ABAC: <c>Cleaning.AssignedHousekeeperUserId ==</c>
    /// caller, in addition to the tenant scoping the Global Query Filter
    /// already applies). Ordered by <see cref="CleaningSummaryResult.ScheduledAtUtc"/>
    /// ascending with nulls last, then <c>createdAtUtc</c>/<c>id</c> as a
    /// deterministic tie-breaker — cleanings with no schedule set are never
    /// hidden, only sorted after every scheduled one.
    /// </summary>
    Task<PagedResult<CleaningSummaryResult>> ListForHousekeeperAsync(
        Guid housekeeperUserId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Self-service detail read (Fase 6, Incremento 2A) — returns <c>null</c>
    /// both when the cleaning does not exist AND when it exists but is not
    /// assigned to <paramref name="housekeeperUserId"/>, so the controller
    /// can fail closed with a uniform 404 without revealing whether a
    /// cleaning belonging to someone else exists (§4 ABAC rule).
    /// </summary>
    Task<CleaningResult?> GetByIdForHousekeeperAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Idempotency guard for the Workflow → Housekeeping
    /// <c>CreateCleaningForReservation</c> flow (Fase 8, Checkpoint 1 —
    /// ADR-018) — <c>true</c> only when a Cleaning already exists for
    /// <paramref name="reservationId"/> with <c>CreatedByUserId == null</c>
    /// (i.e. already created by THIS automated flow), never when only a
    /// manual (<c>CreatedByUserId != null</c>) Cleaning exists for the same
    /// Reservation — those are a separate, deliberately unconstrained case
    /// (see <c>CleaningConfiguration</c>'s own partial-unique-index comment).
    /// Unlike every other member of this interface, this method takes
    /// <paramref name="tenantId"/> explicitly and its implementation opens
    /// its OWN tenant-scoped transaction — it is invoked from the
    /// message-execution boundary, never from the Mediator/HTTP pipeline
    /// this interface's other members rely on for their ambient RLS
    /// transaction (mirrors <c>IPropertyReferenceProjection</c>/
    /// <c>IReservationReferenceProjection</c>'s own self-contained pattern).
    /// </summary>
    Task<bool> ExistsAutomatedForReservationAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);
}
