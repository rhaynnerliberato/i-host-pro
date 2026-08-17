using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Projections;

/// <summary>
/// Dashboard &amp; Reporting's own local, tenant-aware read-model row for a
/// single Reservation — built exclusively from consuming Reservations' own
/// <c>ReservationCreated</c>/<c>ReservationUpdated</c>/<c>ReservationCancelled</c>
/// Integration Events (Fase 7, Incremento 2 — Dashboard &amp; Reporting
/// Foundation, Checkpoint 1). Deliberately carries only the fields the MVP
/// indicators need (check-ins/check-outs in an interval, reservas
/// futuras/canceladas, contagem por status — Checkpoint 0 decision, §5/§23)
/// — no guest name/phone/count. A physically separate table from
/// <c>reservations.reservations</c>, never a foreign key across contexts —
/// same opaque-Guid convention used everywhere in this platform.
/// Infrastructure-only persistence model (never referenced from
/// Application/Domain) — mirrors <c>CleaningScheduleProjectionEntry</c>'s
/// own precedent.
/// </summary>
public sealed class DashboardReservationProjectionEntry : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public DateTimeOffset? CheckInAt { get; private set; }
    public DateTimeOffset? CheckOutAt { get; private set; }
    public string Status { get; private set; } = null!;

    /// <summary>
    /// Out-of-order delivery guard (Checkpoint 0 decision, §29) — an
    /// incoming event only applies if its own <c>IntegrationEvent.Timestamp</c>
    /// is not earlier than the last event actually applied to this row.
    /// </summary>
    public DateTimeOffset LastEventAtUtc { get; private set; }

    private DashboardReservationProjectionEntry()
    {
        // EF Core materialization.
    }

    public DashboardReservationProjectionEntry(
        Guid tenantId, Guid reservationId, Guid propertyId, string status,
        DateTimeOffset? checkInAt, DateTimeOffset? checkOutAt, DateTimeOffset lastEventAtUtc)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        PropertyId = propertyId;
        Status = status;
        CheckInAt = checkInAt;
        CheckOutAt = checkOutAt;
        LastEventAtUtc = lastEventAtUtc;
    }

    /// <summary>Applied on <c>ReservationUpdated</c> — never changes <see cref="Status"/> (that event carries none).</summary>
    public void UpdateSchedule(DateTimeOffset? checkInAt, DateTimeOffset? checkOutAt, DateTimeOffset eventAtUtc)
    {
        CheckInAt = checkInAt;
        CheckOutAt = checkOutAt;
        LastEventAtUtc = eventAtUtc;
    }

    /// <summary>Applied on <c>ReservationCancelled</c> — never changes CheckInAt/CheckOutAt (that event carries none).</summary>
    public void Cancel(DateTimeOffset eventAtUtc)
    {
        Status = "cancelled";
        LastEventAtUtc = eventAtUtc;
    }
}
