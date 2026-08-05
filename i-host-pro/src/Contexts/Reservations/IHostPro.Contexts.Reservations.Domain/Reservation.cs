using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Domain;

/// <summary>
/// A guest's booking of a Property for a date range — the Reservations
/// Bounded Context's central aggregate (Fase 3, Incremento 1 plan). Every
/// Reservation is born <see cref="ReservationStatus.Confirmed"/>;
/// <see cref="ReservationStatus.Cancelled"/> is terminal — no restoration
/// exists this increment. <see cref="CheckInAt"/>/<see cref="CheckOutAt"/>
/// are always stored normalized to UTC — the caller (command handler) is
/// responsible for having required an explicit offset on the way in.
///
/// Guest-count-vs-Property-capacity and date-conflict-vs-other-reservations
/// are deliberately NOT enforced here: both depend on data this aggregate
/// does not own (the Property's current capacity, from Property
/// Management via <c>IPropertyReservationEligibilityReader</c>; other
/// reservations for the same Property, via the advisory-lock-protected
/// conflict check) — mirrors <c>Property</c>'s own split (e.g. it never
/// validates a Condominium actually exists, only that
/// <c>CondominiumId</c>/<c>Address</c> are structurally consistent).
/// </summary>
public sealed class Reservation : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid PropertyId { get; private set; }
    public string GuestName { get; private set; } = null!;
    public string? GuestPhone { get; private set; }
    public DateTimeOffset CheckInAt { get; private set; }
    public DateTimeOffset CheckOutAt { get; private set; }
    public int GuestCount { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Reservation()
    {
        // EF Core materialization.
    }

    private Reservation(
        Guid id, Guid tenantId, Guid propertyId, string guestName, string? guestPhone,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        PropertyId = propertyId;
        GuestName = guestName;
        GuestPhone = guestPhone;
        CheckInAt = checkInAt;
        CheckOutAt = checkOutAt;
        GuestCount = guestCount;
        Status = ReservationStatus.Confirmed;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Reservation Create(
        Guid id, Guid tenantId, Guid propertyId, string guestName, string? guestPhone,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(guestName))
            throw new ArgumentException("Guest name cannot be empty.", nameof(guestName));

        if (guestCount <= 0)
            throw new ArgumentException("Guest count must be greater than zero.", nameof(guestCount));

        var normalizedCheckInAt = checkInAt.ToUniversalTime();
        var normalizedCheckOutAt = checkOutAt.ToUniversalTime();

        if (normalizedCheckOutAt <= normalizedCheckInAt)
            throw new ArgumentException("Check-out must be after check-in.", nameof(checkOutAt));

        return new Reservation(
            id, tenantId, propertyId, guestName.Trim(), NormalizePhone(guestPhone),
            normalizedCheckInAt, normalizedCheckOutAt, guestCount, now);
    }

    /// <summary>
    /// Reassigns this reservation to a different property (Fase 3,
    /// Incremento 1 plan, item 8 — PATCH's <c>propertyId</c> field). The
    /// caller is responsible for having already validated the new property
    /// exists, is <c>Active</c>, and has no conflicting reservation, before
    /// calling this.
    /// </summary>
    public void ChangeProperty(Guid newPropertyId, DateTimeOffset now)
    {
        PropertyId = newPropertyId;
        Touch(now);
    }

    public void ChangeGuestName(string newGuestName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newGuestName))
            throw new ArgumentException("Guest name cannot be empty.", nameof(newGuestName));

        GuestName = newGuestName.Trim();
        Touch(now);
    }

    /// <summary><c>null</c>/whitespace removes the phone — mirrors <c>Property.ChangeAddress(null, ...)</c>.</summary>
    public void ChangeGuestPhone(string? newGuestPhone, DateTimeOffset now)
    {
        GuestPhone = NormalizePhone(newGuestPhone);
        Touch(now);
    }

    /// <summary>
    /// Changes check-in and check-out together — they are interdependent
    /// (check-out must be after check-in), so no single-field mutator for
    /// either exists on its own; the caller determines which of the two
    /// values genuinely changed for <c>ChangedFields</c> purposes BEFORE
    /// calling this (mirrors how the two-field diff already works for
    /// <c>Reservation</c>'s other presence-aware fields).
    /// </summary>
    public void Reschedule(DateTimeOffset newCheckInAt, DateTimeOffset newCheckOutAt, DateTimeOffset now)
    {
        var normalizedCheckInAt = newCheckInAt.ToUniversalTime();
        var normalizedCheckOutAt = newCheckOutAt.ToUniversalTime();

        if (normalizedCheckOutAt <= normalizedCheckInAt)
            throw new ArgumentException("Check-out must be after check-in.", nameof(newCheckOutAt));

        CheckInAt = normalizedCheckInAt;
        CheckOutAt = normalizedCheckOutAt;
        Touch(now);
    }

    /// <summary>
    /// The caller (command handler) is responsible for validating
    /// <paramref name="newGuestCount"/> against the Property's current
    /// capacity BEFORE calling this — this method only enforces the
    /// structural invariant (positive), mirroring how <c>Property.Create</c>
    /// never validates a Condominium actually exists.
    /// </summary>
    public void ChangeGuestCount(int newGuestCount, DateTimeOffset now)
    {
        if (newGuestCount <= 0)
            throw new ArgumentException("Guest count must be greater than zero.", nameof(newGuestCount));

        GuestCount = newGuestCount;
        Touch(now);
    }

    /// <summary>
    /// <see cref="ReservationStatus.Confirmed"/> → <see cref="ReservationStatus.Cancelled"/>
    /// — terminal, no restoration exists this increment. The caller
    /// (<c>CancelReservationCommandHandler</c>) is responsible for
    /// translating an already-cancelled reservation into the precise stable
    /// error code before calling this — this guard is defense-in-depth,
    /// mirrors <c>Property.Activate</c>'s own division of responsibility.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Confirmed)
            throw new InvalidOperationException($"Cannot cancel a reservation in status '{Status}'.");

        Status = ReservationStatus.Cancelled;
        Touch(now);
    }

    private static string? NormalizePhone(string? guestPhone) =>
        string.IsNullOrWhiteSpace(guestPhone) ? null : guestPhone.Trim();

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
