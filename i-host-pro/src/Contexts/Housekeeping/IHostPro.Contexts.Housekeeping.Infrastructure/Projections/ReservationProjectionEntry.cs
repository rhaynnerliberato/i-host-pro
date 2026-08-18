using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Projections;

/// <summary>
/// This context's own local, tenant-aware read-model row for a single
/// Reservation — built exclusively from consuming <c>ReservationCreated</c>
/// (Checkpoint 0/3 gate). Deliberately carries no other field — never
/// <c>PropertyId</c> (approved decision: <c>ReservationUpdated</c> never
/// republishes a changed <c>property_id</c>, so caching it here would risk
/// silent staleness), only existence — a physically separate table from
/// <c>reservations.reservations</c>, never a foreign key across contexts.
/// Infrastructure-only persistence model — <see cref="IReservationReferenceProjection"/>
/// in <c>Housekeeping.Application</c> is the port this entity backs.
///
/// <see cref="IsCancelled"/> (Fase 8, Checkpoint 1 — ADR-018) is a
/// best-effort guard for the Workflow → Housekeeping
/// <c>CreateCleaningForReservation</c> flow: set when this context's own
/// <c>ReservationCancelled</c> reaction runs. Deliberately NOT a
/// consistency guarantee — an independent, unordered RabbitMQ queue means
/// this flag can still be stale at the exact instant a racing
/// <c>CreateCleaningForReservation</c> command is processed (accepted risk,
/// documented in ADR-018).
/// </summary>
public sealed class ReservationProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public bool IsCancelled { get; private set; }

    private ReservationProjectionEntry()
    {
        // EF Core materialization.
    }

    public ReservationProjectionEntry(Guid tenantId, Guid reservationId)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
    }

    public void MarkCancelled() => IsCancelled = true;
}
