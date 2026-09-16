namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

/// <summary>
/// Result of parsing one Airbnb "reservation reminder" email into the fields
/// <c>Reservation.CreateImported</c> requires. Deliberately carries no raw
/// email text — only the specific extracted values (or a structured failure)
/// — so a caller can log/report outcomes without ever holding the full
/// message body.
/// </summary>
public sealed record AirbnbReservationReminderParseResult
{
    public bool IsSuccess { get; }
    public string? ExternalReservationId { get; }
    public string? GuestName { get; }
    public DateTimeOffset? CheckInAt { get; }
    public DateTimeOffset? CheckOutAt { get; }
    public int? GuestCount { get; }
    public string? ListingName { get; }
    public AirbnbReservationReminderParseFailureReason? FailureReason { get; }
    public string? MissingFieldName { get; }

    private AirbnbReservationReminderParseResult(
        bool isSuccess, string? externalReservationId, string? guestName, DateTimeOffset? checkInAt,
        DateTimeOffset? checkOutAt, int? guestCount, string? listingName,
        AirbnbReservationReminderParseFailureReason? failureReason, string? missingFieldName)
    {
        IsSuccess = isSuccess;
        ExternalReservationId = externalReservationId;
        GuestName = guestName;
        CheckInAt = checkInAt;
        CheckOutAt = checkOutAt;
        GuestCount = guestCount;
        ListingName = listingName;
        FailureReason = failureReason;
        MissingFieldName = missingFieldName;
    }

    public static AirbnbReservationReminderParseResult Success(
        string externalReservationId, string guestName, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
        int guestCount, string listingName) =>
        new(true, externalReservationId, guestName, checkInAt, checkOutAt, guestCount, listingName, null, null);

    public static AirbnbReservationReminderParseResult Failure(
        AirbnbReservationReminderParseFailureReason reason, string? missingFieldName = null) =>
        new(false, null, null, null, null, null, null, reason, missingFieldName);
}
