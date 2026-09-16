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

    /// <summary>
    /// Automatic Publication Design + Safety gate — a non-error terminal
    /// state for a message that would otherwise have auto-published but was
    /// received before the tenant's own <c>AutoPublishNotBeforeUtc</c>
    /// cutoff (historical-import protection). Deliberately narrow: this
    /// value is set ONLY for that one deterministic, timestamp-based case -
    /// never used as a general "not a reservation" bucket for
    /// payment/review/other non-reservation Airbnb mail, since reliably
    /// classifying those from the parser's own UnsupportedTemplate outcome
    /// alone would require an unvalidated content classifier this feature
    /// has no real evidence for yet (flagged explicitly, not guessed).
    /// </summary>
    Ignored = 4,
}
