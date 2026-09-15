namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Processing state of one <see cref="AirbnbEmailMessageReceipt"/> — the
/// Airbnb Email Bridge's own ingestion-level idempotency record, distinct
/// from <c>Reservation.ExternalReservationId</c>'s reservation-level
/// deduplication (a Graph delta replay must never be processed twice, even
/// before any reservation identifier has been extracted).
/// </summary>
public enum AirbnbEmailMessageProcessingStatus
{
    Pending = 0,
    Processed = 1,
    NeedsReview = 2,
    Failed = 3,
}
