namespace IHostPro.Contexts.Housekeeping.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Housekeeping commands/queries (Fase 6, Incremento 1) — used as
/// <see cref="IHostPro.BuildingBlocks.Domain.Error.Code"/>, never paired
/// with a hardcoded message. Snake_case (not the dotted
/// <c>"Reservations.ReservationNotFound"</c> style
/// <c>ReservationsErrorCodes</c> uses) — the exact stable codes this
/// increment's approval explicitly requires the API's <c>ProblemDetails</c>
/// to expose to the client, unlike Reservations' own generic
/// "not_found"/"conflict" titles. Every code here corresponds to a real,
/// implemented failure path — none added speculatively.
/// </summary>
public static class HousekeepingErrorCodes
{
    /// <summary>
    /// An administrative lookup/action for a cleaning id that does not
    /// exist, or that belongs to a different tenant — the two are
    /// indistinguishable by design, same convention as every other
    /// cross-tenant lookup in this platform.
    /// </summary>
    public const string CleaningNotFound = "cleaning_not_found";

    /// <summary>
    /// <c>POST /api/v1/cleanings</c> referenced a <c>propertyId</c> that is
    /// not a known, active property of the caller's tenant — resolved via
    /// this context's own local projection of Property Management's
    /// lifecycle events (Checkpoint 0/3), never a direct query against
    /// Property Management's own schema.
    /// </summary>
    public const string PropertyNotFound = "property_not_found";

    /// <summary>
    /// <c>POST /api/v1/cleanings</c> referenced a <c>reservationId</c> that
    /// is not known to this context's local Reservations projection for the
    /// caller's tenant — never used to derive <c>propertyId</c> (Checkpoint
    /// 0 gate), only to validate the reference itself.
    /// </summary>
    public const string ReservationReferenceNotAvailable = "reservation_reference_not_available";

    /// <summary>
    /// The assignee referenced by <c>POST /api/v1/cleanings/{id}/assign</c>
    /// does not exist for the tenant, is not active, or does not hold the
    /// <c>HOUSEKEEPER</c> role — resolved via
    /// <c>IIdentityUserEligibilityReader</c>.
    /// </summary>
    public const string HousekeeperNotEligible = "housekeeper_not_eligible";

    /// <summary>
    /// A lifecycle action (assign/start/start-inspection/complete/cancel/
    /// mark-interrupted/mark-waiting-materials/mark-waiting-help) was
    /// attempted from a status Documento 06 does not document as a valid
    /// source for that specific transition — translated from
    /// <see cref="Domain.Cleaning"/>'s own guard <c>InvalidOperationException</c>.
    /// </summary>
    public const string InvalidCleaningTransition = "invalid_cleaning_transition";

    /// <summary>
    /// A write to the <c>housekeeping.cleanings</c> row lost a genuine
    /// optimistic concurrency race on its <c>xmin</c> token to another
    /// concurrent write of the SAME cleaning — translated from a caught
    /// <c>DbUpdateConcurrencyException</c>, never retried automatically.
    /// </summary>
    public const string CleaningConcurrencyConflict = "cleaning_concurrency_conflict";
}
