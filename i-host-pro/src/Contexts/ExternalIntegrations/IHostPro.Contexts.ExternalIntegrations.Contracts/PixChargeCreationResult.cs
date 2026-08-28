namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral PIX charge creation outcome (ADR-025). Never carries the
/// provider's raw response body or any provider-specific DTO. <see cref="QrCodePayload"/>
/// is returned here, synchronously, at creation time — Payments persists it
/// on the <c>PixCharge</c> aggregate itself (an explicit product decision;
/// see ADR-025) so a later, asynchronous delivery read
/// (<c>IPixChargeDeliveryReader</c>, ADR-027) can return it without ever
/// calling the provider a second time.
/// </summary>
public sealed record PixChargeCreationResult(
    bool Accepted,
    string? ProviderChargeId,
    string? QrCodePayload,
    DateTimeOffset? ExpiresAtUtc,
    string? FailureCode);
