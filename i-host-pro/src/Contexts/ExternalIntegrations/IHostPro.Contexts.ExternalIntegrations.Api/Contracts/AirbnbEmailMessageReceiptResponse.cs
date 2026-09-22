namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record AirbnbEmailMessageReceiptResponse(
    Guid Id,
    DateTimeOffset ReceivedAtUtc,
    string ProcessingStatus,
    string? DetectedEventType,
    string? ParserVersion,
    string? FailureReason,
    string? UnmatchedListingTitle,
    DateTimeOffset? ProcessedAtUtc,
    DateTimeOffset CreatedAtUtc);
