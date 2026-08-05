namespace IHostPro.Contexts.Reservations.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Reservations commands/queries (Fase 3, Incremento 1 plan, item 10) —
/// mirrors <c>PropertyManagementErrorCodes</c>'s own convention exactly:
/// used as <see cref="IHostPro.BuildingBlocks.Domain.Error.Code"/>, never
/// paired with a hardcoded message.
/// </summary>
public static class ReservationsErrorCodes
{
    /// <summary>
    /// An administrative lookup/update for a reservation id that does not
    /// exist, or that belongs to a different tenant — the two are
    /// indistinguishable by design, same convention as every other
    /// cross-tenant lookup in this platform.
    /// </summary>
    public const string ReservationNotFound = "Reservations.ReservationNotFound";

    /// <summary>
    /// <c>POST /api/v1/reservations</c> or <c>PATCH /api/v1/reservations/{id}</c>
    /// referenced a <c>propertyId</c> that does not exist for the caller's
    /// tenant, or belongs to a different tenant — resolved via
    /// <c>IPropertyReservationEligibilityReader</c>, never a direct query
    /// against Property Management's own schema.
    /// </summary>
    public const string PropertyNotFound = "Reservations.PropertyNotFound";

    /// <summary>
    /// The referenced property exists but is not <c>Active</c> — a
    /// reservation may only be created/moved to a property currently able to
    /// receive one (Fase 3, Incremento 1 plan, item 6). A property already
    /// holding CONFIRMED reservations that is later deactivated keeps those
    /// reservations untouched — this code only blocks NEW/reassigned
    /// bookings.
    /// </summary>
    public const string PropertyNotActive = "Reservations.PropertyNotActive";

    /// <summary>
    /// <see cref="Reservations.CreateReservationCommand.GuestCount"/> (or its
    /// PATCH equivalent) exceeds the referenced property's current capacity.
    /// </summary>
    public const string PropertyCapacityExceeded = "Reservations.PropertyCapacityExceeded";

    /// <summary>
    /// The requested (or revalidated, on PATCH) check-in/check-out interval
    /// overlaps an existing <c>Confirmed</c> reservation for the same
    /// property — start inclusive, end exclusive (Fase 3, Incremento 1 plan,
    /// item 7). Checked under an advisory-lock-protected transaction, never
    /// racy.
    /// </summary>
    public const string ReservationDateConflict = "Reservations.ReservationDateConflict";

    /// <summary>
    /// <c>POST /api/v1/reservations/{id}/cancel</c> called on a reservation
    /// already <c>Cancelled</c> — terminal, no restoration exists this
    /// increment.
    /// </summary>
    public const string ReservationAlreadyCancelled = "Reservations.ReservationAlreadyCancelled";

    /// <summary>
    /// <c>PATCH /api/v1/reservations/{id}</c> called on a reservation already
    /// <c>Cancelled</c> — rejected before any diffing or no-op evaluation,
    /// even when every supplied field already matches the current value
    /// (mirrors Property Management's <c>ArchivedPropertyCannotBeModified</c>
    /// precedent).
    /// </summary>
    public const string CancelledReservationCannotBeModified = "Reservations.CancelledReservationCannotBeModified";

    /// <summary>
    /// <c>PATCH /api/v1/reservations/{id}</c> or the cancel endpoint lost a
    /// genuine optimistic concurrency race on the <c>reservations</c> row's
    /// <c>xmin</c> token to another concurrent write of the SAME reservation
    /// — translated from a caught <c>DbUpdateConcurrencyException</c>, never
    /// retried automatically.
    /// </summary>
    public const string ReservationConcurrencyConflict = "Reservations.ReservationConcurrencyConflict";

    /// <summary>
    /// <c>PATCH /api/v1/reservations/{id}</c> called with none of the six
    /// presence-aware fields provided — rejected before any lookup, since
    /// there is nothing to change (mirrors Property Management's
    /// <c>NoChangesProvided</c> precedent).
    /// </summary>
    public const string NoChangesProvided = "Reservations.NoChangesProvided";
}
