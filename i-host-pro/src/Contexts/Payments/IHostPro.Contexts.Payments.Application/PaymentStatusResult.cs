namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Minimal payment-status projection for a single Reservation (Fase 11,
/// Checkpoint 3 — AI Agent's own <c>GetPaymentStatus</c> Read Tool).
/// Deliberately excludes <c>PixChargeId</c>/<c>ProviderChargeId</c>/
/// <c>QrCodePayload</c>/<c>IdempotencyKey</c>/payer data/provider-specific
/// failure detail — <see cref="Status"/> is returned verbatim
/// (Pending/Confirmed/Failed/Expired/Cancelled), never an LLM-facing
/// interpreted conclusion.
/// </summary>
public sealed record PaymentStatusResult(string Status, decimal Amount, string CurrencyCode, DateTimeOffset? ExpiresAtUtc);
