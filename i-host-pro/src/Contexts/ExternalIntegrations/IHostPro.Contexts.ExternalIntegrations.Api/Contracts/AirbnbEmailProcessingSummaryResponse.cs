namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>Minimal Operations/UX gate — counts only, never guest/mailbox/reservation data or individual receipts.</summary>
public sealed record AirbnbEmailProcessingSummaryResponse(
    Guid TenantId, int Pending, int Processed, int NeedsReview, int Failed, int Ignored);
