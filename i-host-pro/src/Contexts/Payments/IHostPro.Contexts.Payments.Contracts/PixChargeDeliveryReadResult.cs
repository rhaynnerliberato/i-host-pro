namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IPixChargeDeliveryReader"/> returns
/// to Communication (Fase 10, Checkpoint 5 — ADR-027, synchronous exception
/// #11) — never the <c>PixCharge</c> aggregate itself, never
/// <c>ProviderChargeId</c>, never <c>IdempotencyKey</c>, never any provider
/// secret/raw provider response.
/// </summary>
public sealed record PixChargeDeliveryReadResult(
    Guid PixChargeId,
    string QrCodePayload,
    decimal Amount,
    string CurrencyCode,
    DateTimeOffset? ExpiresAtUtc);
