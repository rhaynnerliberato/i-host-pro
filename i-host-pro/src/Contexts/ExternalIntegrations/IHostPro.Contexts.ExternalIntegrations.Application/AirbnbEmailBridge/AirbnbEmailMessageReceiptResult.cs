namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// One receipt as exposed to an operator (list item and detail — the same
/// shape serves both, since no field is detail-only) — Airbnb Email
/// Operational Exception Resolution gate. Deliberately excludes
/// <c>GraphMessageId</c>/<c>InternetMessageId</c>, the raw email body, guest
/// PII and tokens: none of those are needed to understand or resolve an
/// exception, and none are persisted on the receipt anyway except the two
/// Graph identifiers, which stay internal to this Bounded Context.
/// </summary>
public sealed record AirbnbEmailMessageReceiptResult(
    Guid Id,
    DateTimeOffset ReceivedAtUtc,
    string ProcessingStatus,
    string? DetectedEventType,
    string? ParserVersion,
    string? FailureReason,
    string? UnmatchedListingTitle,
    DateTimeOffset? ProcessedAtUtc,
    DateTimeOffset CreatedAtUtc);
