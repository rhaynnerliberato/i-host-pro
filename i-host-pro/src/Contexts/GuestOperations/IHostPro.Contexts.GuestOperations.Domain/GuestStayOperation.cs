using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Domain;

/// <summary>
/// The guest-facing operational counterpart to a Reservation (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation): tracks the real-world
/// check-in/checkout lifecycle for exactly one Reservation, independently of
/// Reservations' own booking-lifecycle aggregate. Deliberately minimal this
/// checkpoint — no guest name/phone, no access credential, no Early/Late
/// request, no Portaria, no payment — all deferred to later checkpoints.
///
/// <see cref="ReservationId"/>/<see cref="PropertyId"/> carry NO physical
/// foreign key to <c>reservations.reservations</c>/
/// <c>property_management.properties</c> (mirrors <c>Reservation.PropertyId</c>'s
/// own opaque-Guid convention across Bounded Context boundaries). Exactly
/// one <see cref="GuestStayOperation"/> per Reservation is enforced by a
/// database-level unique constraint on (<see cref="TenantId"/>,
/// <see cref="ReservationId"/>), never a physical FK.
/// </summary>
public sealed class GuestStayOperation : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public GuestStayOperationStatus Status { get; private set; }
    public DateTimeOffset? CheckedInAtUtc { get; private set; }
    public DateTimeOffset? CheckedOutAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private GuestStayOperation()
    {
        // EF Core materialization.
    }

    private GuestStayOperation(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        PropertyId = propertyId;
        Status = GuestStayOperationStatus.Active;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static GuestStayOperation Create(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId, DateTimeOffset now)
    {
        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation id cannot be empty.", nameof(reservationId));

        if (propertyId == Guid.Empty)
            throw new ArgumentException("Property id cannot be empty.", nameof(propertyId));

        return new GuestStayOperation(id, tenantId, reservationId, propertyId, now);
    }

    /// <summary>
    /// <see cref="GuestStayOperationStatus.Active"/> → <see cref="GuestStayOperationStatus.CheckedOut"/>
    /// — terminal, no restoration exists this checkpoint. The caller
    /// (<c>RecordGuestCheckedOutCommandHandler</c>) is responsible for having
    /// already translated an already-checked-out operation into a silent
    /// idempotent no-op BEFORE calling this — this guard is
    /// defense-in-depth, mirrors <c>Reservation.Cancel</c>'s own division of
    /// responsibility.
    /// </summary>
    public void CheckOut(DateTimeOffset now)
    {
        if (Status != GuestStayOperationStatus.Active)
            throw new InvalidOperationException($"Cannot check out a guest stay operation in status '{Status}'.");

        Status = GuestStayOperationStatus.CheckedOut;
        CheckedOutAtUtc = now;
        UpdatedAtUtc = now;
    }
}
