namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral PIX charge creation request (ADR-025). Deliberately
/// carries no payer PII (no CPF/CNPJ/email/phone) — this checkpoint's
/// approved scope never collects payer data (Fase 10, Checkpoint 5 mandate
/// item 46). <see cref="IdempotencyKey"/> is <see cref="Domain.PixCharge.IdempotencyKey"/>,
/// generated internally by Payments — never provider-supplied.
/// </summary>
public sealed record PixChargeRequest(
    Guid TenantId,
    Guid PixChargeId,
    Guid IdempotencyKey,
    decimal Amount,
    string CurrencyCode);
