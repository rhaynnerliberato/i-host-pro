namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// Provider-neutral fact delivered to Payments: "a PIX charge expired"
/// (Fase 10, Checkpoint 5.1 — Payment Failure/Expiration Evidence
/// Corrective Gate). Mirrors <see cref="PixChargeConfirmationReceived"/>'s
/// own shape, placement, and reasoning exactly — a cross-context message
/// living in the RECEIVING context's own Contracts project, not an
/// <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>.
///
/// Same "no real provider/webhook yet" scope boundary as
/// <see cref="PixChargeConfirmationReceived"/>: the only publisher today is
/// the E2E test harness, simulating the provider-neutral fact via a real
/// Wolverine/RabbitMQ send. The Payments-side handler is genuine production
/// code representing the seam a FUTURE ExternalIntegrations
/// webhook-normalization step is expected to publish through, unchanged.
///
/// Payload is deliberately minimal — zero PII, zero provider-specific data:
/// no provider DTO, no QR/copy-paste payload, no payer data, no provider
/// secret.
/// </summary>
public sealed record PixChargeExpirationReceived
{
    public required Guid TenantId { get; init; }

    public required Guid PixChargeId { get; init; }

    public required DateTimeOffset ExpiredAtUtc { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
