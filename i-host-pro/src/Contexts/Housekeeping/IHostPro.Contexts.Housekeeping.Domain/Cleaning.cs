using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Domain;

/// <summary>
/// A single cleaning cycle of a Property — the Housekeeping Bounded
/// Context's central aggregate (Fase 6, Incremento 1 plan). Every Cleaning
/// is born <see cref="CleaningStatus.Pending"/>, created administratively
/// (Checkpoint 0 gate: never auto-derived from a Reservation's checkout —
/// that trigger belongs to Fase 10, not yet implemented).
///
/// <see cref="PropertyId"/> is always caller-supplied, never derived from
/// <see cref="ReservationId"/>'s own projection (Checkpoint 0 gate:
/// <c>ReservationUpdated</c> never republishes a changed <c>property_id</c>,
/// so deriving it would risk silent staleness — approved decision). Carries
/// no physical foreign key to <c>property_management.properties</c> or
/// <c>reservations.reservations</c> — same opaque-Guid convention as
/// <c>Reservation.PropertyId</c>.
/// </summary>
public sealed class Cleaning : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid? ReservationId { get; private set; }
    public Guid? AssignedHousekeeperUserId { get; private set; }
    public CleaningStatus Status { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? InspectionStartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }

    private Cleaning()
    {
        // EF Core materialization.
    }

    private Cleaning(
        Guid id, Guid tenantId, Guid propertyId, Guid? reservationId, Guid createdByUserId, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        ReservationId = reservationId;
        CreatedByUserId = createdByUserId;
        Status = CleaningStatus.Pending;
        CreatedAtUtc = now;
    }

    public static Cleaning Create(
        Guid id, Guid tenantId, Guid propertyId, Guid? reservationId, Guid createdByUserId, DateTimeOffset now) =>
        new(id, tenantId, propertyId, reservationId, createdByUserId, now.ToUniversalTime());

    /// <summary>
    /// <see cref="CleaningStatus.Pending"/> → <see cref="CleaningStatus.Assigned"/>.
    /// The caller (command handler) is responsible for having already
    /// validated <paramref name="housekeeperUserId"/> is active, belongs to
    /// the tenant, and holds the <c>HOUSEKEEPER</c> role (via
    /// <c>IIdentityUserEligibilityReader</c>) BEFORE calling this — this
    /// guard only enforces the state transition itself.
    /// </summary>
    public void Assign(Guid housekeeperUserId, DateTimeOffset now)
    {
        if (Status != CleaningStatus.Pending)
            throw new InvalidOperationException($"Cannot assign a cleaning in status '{Status}'.");

        AssignedHousekeeperUserId = housekeeperUserId;
        Status = CleaningStatus.Assigned;
    }

    /// <summary>
    /// <see cref="CleaningStatus.Assigned"/> → <see cref="CleaningStatus.Started"/>
    /// — the optional <see cref="CleaningStatus.InTransit"/> step is skipped
    /// by design this increment (Checkpoint 0/§6 approval decision: not
    /// required, no admin-triggered entry point exists).
    /// </summary>
    public void Start(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Assigned)
            throw new InvalidOperationException($"Cannot start a cleaning in status '{Status}'.");

        Status = CleaningStatus.Started;
        StartedAtUtc = now.ToUniversalTime();
    }

    /// <summary><see cref="CleaningStatus.Started"/> → <see cref="CleaningStatus.InInspection"/>.</summary>
    public void StartInspection(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Started)
            throw new InvalidOperationException($"Cannot start inspection for a cleaning in status '{Status}'.");

        Status = CleaningStatus.InInspection;
        InspectionStartedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// <see cref="CleaningStatus.InInspection"/> → <see cref="CleaningStatus.Completed"/>
    /// — terminal, no restoration exists this increment.
    /// </summary>
    public void Complete(DateTimeOffset now)
    {
        if (Status != CleaningStatus.InInspection)
            throw new InvalidOperationException($"Cannot complete a cleaning in status '{Status}'.");

        Status = CleaningStatus.Completed;
        CompletedAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// <see cref="CleaningStatus.Pending"/>/<see cref="CleaningStatus.Assigned"/>
    /// → <see cref="CleaningStatus.Cancelled"/> — terminal. Documento 06's
    /// own "Fluxos alternativos" only documents <c>Designada → Cancelada</c>
    /// explicitly; cancellation from <see cref="CleaningStatus.Pending"/> is
    /// additionally allowed here as the natural, lowest-risk extension (a
    /// cleaning with no cleaning work yet started), not as an invented
    /// mid-lifecycle transition — cancellation from <see cref="CleaningStatus.Started"/>/
    /// <see cref="CleaningStatus.InInspection"/> onward is deliberately NOT
    /// implemented (no documented transition), registered as a gap in the
    /// Fase 6 homologation document.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (CleaningStatus.Pending or CleaningStatus.Assigned))
            throw new InvalidOperationException($"Cannot cancel a cleaning in status '{Status}'.");

        Status = CleaningStatus.Cancelled;
        CancelledAtUtc = now.ToUniversalTime();
    }

    /// <summary>
    /// <see cref="CleaningStatus.Started"/> → <see cref="CleaningStatus.Interrupted"/>
    /// — Documento 06 documents this entry transition explicitly; it
    /// documents no transition back, so none exists this increment
    /// (registered as a gap, same treatment as Reopen).
    /// </summary>
    public void MarkInterrupted(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Started)
            throw new InvalidOperationException($"Cannot interrupt a cleaning in status '{Status}'.");

        Status = CleaningStatus.Interrupted;
    }

    /// <summary>
    /// <see cref="CleaningStatus.Started"/> → <see cref="CleaningStatus.WaitingMaterials"/>
    /// — same documented-entry/undocumented-return treatment as
    /// <see cref="MarkInterrupted"/>.
    /// </summary>
    public void MarkWaitingMaterials(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Started)
            throw new InvalidOperationException($"Cannot mark a cleaning in status '{Status}' as waiting for materials.");

        Status = CleaningStatus.WaitingMaterials;
    }

    /// <summary>
    /// <see cref="CleaningStatus.Started"/> → <see cref="CleaningStatus.WaitingHelp"/>
    /// — same documented-entry/undocumented-return treatment as
    /// <see cref="MarkInterrupted"/>.
    /// </summary>
    public void MarkWaitingHelp(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Started)
            throw new InvalidOperationException($"Cannot mark a cleaning in status '{Status}' as waiting for help.");

        Status = CleaningStatus.WaitingHelp;
    }
}
