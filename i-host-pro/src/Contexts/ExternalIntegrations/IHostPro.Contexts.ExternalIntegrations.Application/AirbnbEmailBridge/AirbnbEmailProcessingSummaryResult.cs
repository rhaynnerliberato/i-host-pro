namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Airbnb Email Bridge Minimal Operations/UX gate — tenant-scoped counts of
/// <see cref="Domain.AirbnbEmailMessageReceipt"/> by
/// <see cref="Domain.AirbnbEmailMessageProcessingStatus"/>. Counts only — no
/// guest data, mailbox data, GraphMessageId, reservation code, email
/// subject/body, listing title, or failure detail. Individual receipt
/// listing is a deliberately deferred, separate future decision.
/// </summary>
public sealed record AirbnbEmailProcessingSummaryResult(
    Guid TenantId, int Pending, int Processed, int NeedsReview, int Failed, int Ignored);
