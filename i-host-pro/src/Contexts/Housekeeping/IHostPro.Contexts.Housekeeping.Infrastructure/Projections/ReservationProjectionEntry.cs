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
/// </summary>
public sealed class ReservationProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }

    private ReservationProjectionEntry()
    {
        // EF Core materialization.
    }

    public ReservationProjectionEntry(Guid tenantId, Guid reservationId)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
    }
}
