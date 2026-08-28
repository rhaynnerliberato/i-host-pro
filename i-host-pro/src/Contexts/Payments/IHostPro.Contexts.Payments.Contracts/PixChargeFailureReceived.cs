namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// Provider-neutral fact delivered to Payments: "a PIX charge failed" (Fase
/// 10, Checkpoint 5.1 — Payment Failure/Expiration Evidence Corrective
/// Gate). Mirrors <see cref="PixChargeConfirmationReceived"/>'s own shape,
/// placement, and reasoning exactly — a cross-context message living in the
/// RECEIVING context's own Contracts project, not an
/// <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>.
///
/// Same "no real provider/webhook yet" scope boundary as
/// <see cref="PixChargeConfirmationReceived"/>: the only publisher today is
/// the E2E test harness, simulating the provider-neutral fact via a real
/// Wolverine/RabbitMQ send. The Payments-side handler is genuine production
/// code representing the seam a FUTURE ExternalIntegrations
/// webhook-normalization step is expected to publish through, unchanged.
///
/// <see cref="FailureCode"/> is optional, provider-neutral/sanitized, and
/// used for diagnostics/logging only — <c>PixCharge</c> itself has no
/// column for it (mirrors how <c>IPixProvider</c>'s own
/// <c>PixChargeCreationResult.FailureCode</c> is already only logged, never
/// persisted, by <c>LateCheckoutPaymentRequiredChargeInitializer</c>).
/// Payload is otherwise deliberately minimal: no provider DTO, no
/// QR/copy-paste payload, no payer PII, no provider secret.
/// </summary>
public sealed record PixChargeFailureReceived
{
    public required Guid TenantId { get; init; }

    public required Guid PixChargeId { get; init; }

    public string? FailureCode { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
