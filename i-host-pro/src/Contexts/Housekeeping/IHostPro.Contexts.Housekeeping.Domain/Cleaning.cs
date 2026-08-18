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

    /// <summary>
    /// The Administrator/Operator who created this cleaning — <c>null</c>
    /// when it was created automatically by a system reaction (Fase 8,
    /// Checkpoint 1 — ADR-018: Workflow Orchestration's <c>ReservationCreated</c>
    /// → <c>CreateCleaningForReservation</c> flow) rather than through the
    /// authenticated administrative HTTP flow. No "system user" identity is
    /// seeded for this — <c>null</c> is the actor, mirroring the existing
    /// <c>ActorType = "System", ActorId = null</c> precedent already used by
    /// <c>ReservationProjectionAndCancellationReaction</c> for automated
    /// Integration Events.
    /// </summary>
    public Guid? CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// When this cleaning is expected to happen — always caller-supplied at
    /// creation, optional, never derived/inferred (Fase 6, Incremento 2A,
    /// Checkpoint 0 gap resolution: <c>CreatedAtUtc</c> alone cannot answer
    /// "when should this cleaning happen", the Portal's "próximas faxinas"
    /// requirement). A cleaning with no value here simply does not
    /// participate in schedule-based ordering; it is never hidden or
    /// defaulted to a guessed date.
    /// </summary>
    public DateTimeOffset? ScheduledAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? InspectionStartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }

    private Cleaning()
    {
        // EF Core materialization.
    }

    private Cleaning(
        Guid id, Guid tenantId, Guid propertyId, Guid? reservationId, Guid? createdByUserId, DateTimeOffset now,
        DateTimeOffset? scheduledAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        ReservationId = reservationId;
        CreatedByUserId = createdByUserId;
        Status = CleaningStatus.Pending;
        CreatedAtUtc = now;
        ScheduledAtUtc = scheduledAtUtc?.ToUniversalTime();
    }

    public static Cleaning Create(
        Guid id, Guid tenantId, Guid propertyId, Guid? reservationId, Guid? createdByUserId, DateTimeOffset now,
        DateTimeOffset? scheduledAtUtc = null) =>
        new(id, tenantId, propertyId, reservationId, createdByUserId, now.ToUniversalTime(), scheduledAtUtc);

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
    /// <see cref="CleaningStatus.Assigned"/> → <see cref="CleaningStatus.InTransit"/>
    /// — self-service only (Fase 6, Incremento 2A): Documento 06 describes
    /// this step as the housekeeper informing their own displacement
    /// ("Faxineira informou deslocamento"), never an administrative action.
    /// Documento 06 also marks it "Opcional. Configuração por tenant." — no
    /// per-tenant configuration mechanism backs that optionality yet
    /// (Checkpoint 0 gap resolution), so this step simply remains optional
    /// in the literal sense that <see cref="Start"/> accepts either
    /// <see cref="CleaningStatus.Assigned"/> or <see cref="CleaningStatus.InTransit"/>
    /// as its origin.
    /// </summary>
    public void MarkInTransit(DateTimeOffset now)
    {
        if (Status != CleaningStatus.Assigned)
            throw new InvalidOperationException($"Cannot mark a cleaning in status '{Status}' as in transit.");

        Status = CleaningStatus.InTransit;
    }

    /// <summary>
    /// <see cref="CleaningStatus.Assigned"/> OR <see cref="CleaningStatus.InTransit"/>
    /// → <see cref="CleaningStatus.Started"/> — <see cref="CleaningStatus.InTransit"/>
    /// accepted as a valid origin since Fase 6, Incremento 2A (see
    /// <see cref="MarkInTransit"/>); both direct and via-InTransit paths
    /// remain valid, matching Documento 06's own "opcional" framing.
    /// </summary>
    public void Start(DateTimeOffset now)
    {
        if (Status is not (CleaningStatus.Assigned or CleaningStatus.InTransit))
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
