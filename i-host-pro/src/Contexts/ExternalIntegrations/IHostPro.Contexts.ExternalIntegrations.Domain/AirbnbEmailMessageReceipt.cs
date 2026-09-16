using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// The Airbnb Email Bridge's own ingestion-level idempotency record for one
/// Microsoft Graph message — bespoke to this bridge, deliberately not a
/// generic inbox/message-log abstraction (ADR-022/ADR-023: no speculative
/// framework). Unique on (TenantId, GraphMessageId): a Graph delta replay (or
/// a crash between processing a message and advancing the sync cursor) must
/// never be processed twice.
///
/// This is a different idempotency concern than
/// <c>Reservation.ExternalReservationId</c>'s own uniqueness constraint: a
/// receipt can exist — and be <see cref="AirbnbEmailMessageProcessingStatus.NeedsReview"/>
/// or <see cref="AirbnbEmailMessageProcessingStatus.Failed"/> — long before
/// (or without ever) resolving to a reservation.
/// </summary>
public sealed class AirbnbEmailMessageReceipt : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string GraphMessageId { get; private set; } = null!;
    public string? InternetMessageId { get; private set; }
    public DateTimeOffset ReceivedAtUtc { get; private set; }
    public AirbnbEmailMessageProcessingStatus ProcessingStatus { get; private set; }
    public string? DetectedEventType { get; private set; }
    public string? ExternalReservationId { get; private set; }
    public string? ParserVersion { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AirbnbEmailMessageReceipt()
    {
        // EF Core materialization.
    }

    private AirbnbEmailMessageReceipt(
        Guid id, Guid tenantId, string graphMessageId, string? internetMessageId, DateTimeOffset receivedAtUtc,
        DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        GraphMessageId = graphMessageId;
        InternetMessageId = internetMessageId;
        ReceivedAtUtc = receivedAtUtc;
        ProcessingStatus = AirbnbEmailMessageProcessingStatus.Pending;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbEmailMessageReceipt Create(
        Guid id, Guid tenantId, string graphMessageId, string? internetMessageId, DateTimeOffset receivedAtUtc,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(graphMessageId))
            throw new ArgumentException("Graph message id cannot be empty.", nameof(graphMessageId));

        return new AirbnbEmailMessageReceipt(
            id, tenantId, graphMessageId.Trim(), internetMessageId, receivedAtUtc, createdAtUtc);
    }

    public void MarkProcessed(
        string? detectedEventType, string? externalReservationId, string parserVersion, DateTimeOffset processedAtUtc)
    {
        ProcessingStatus = AirbnbEmailMessageProcessingStatus.Processed;
        DetectedEventType = detectedEventType;
        ExternalReservationId = externalReservationId;
        ParserVersion = parserVersion;
        ProcessedAtUtc = processedAtUtc;
    }

    public void MarkNeedsReview(string? detectedEventType, string parserVersion, DateTimeOffset processedAtUtc)
    {
        ProcessingStatus = AirbnbEmailMessageProcessingStatus.NeedsReview;
        DetectedEventType = detectedEventType;
        ParserVersion = parserVersion;
        ProcessedAtUtc = processedAtUtc;
    }

    /// <summary><paramref name="failureReason"/> must never contain the raw email body, tokens, or guest PII — a bounded, sanitized diagnostic code/message only.</summary>
    public void MarkFailed(string failureReason, string? parserVersion, DateTimeOffset processedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
            throw new ArgumentException("Failure reason cannot be empty.", nameof(failureReason));

        ProcessingStatus = AirbnbEmailMessageProcessingStatus.Failed;
        FailureReason = failureReason;
        ParserVersion = parserVersion;
        ProcessedAtUtc = processedAtUtc;
    }

    /// <summary>
    /// Automatic Publication Design + Safety gate — a message that parsed
    /// successfully and resolved to a Property (would otherwise have
    /// published) but predates the tenant's own <c>AutoPublishNotBeforeUtc</c>
    /// cutoff. Never a technical failure — <see cref="FailureReason"/> is
    /// left null.
    /// </summary>
    public void MarkIgnored(string? detectedEventType, string parserVersion, DateTimeOffset processedAtUtc)
    {
        ProcessingStatus = AirbnbEmailMessageProcessingStatus.Ignored;
        DetectedEventType = detectedEventType;
        ParserVersion = parserVersion;
        ProcessedAtUtc = processedAtUtc;
    }
}
