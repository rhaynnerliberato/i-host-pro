using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Manually reprocesses one terminal receipt (Airbnb Email Operational
/// Exception Resolution gate) — retryable only for <c>NeedsReview</c> (once
/// its listing title now has an exact mapping) or <c>Failed</c> with reason
/// <c>PublisherFailure</c>; every other status/reason is rejected. See
/// <see cref="RetryAirbnbEmailMessageReceiptCommandHandler"/> for the full flow.
/// </summary>
public sealed record RetryAirbnbEmailMessageReceiptCommand(Guid TenantId, Guid ReceiptId, Guid ActorUserId)
    : ICommand<AirbnbEmailMessageReceiptResult>;
