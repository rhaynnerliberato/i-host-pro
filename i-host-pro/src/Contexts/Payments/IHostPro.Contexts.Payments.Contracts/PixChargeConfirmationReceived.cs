namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// Provider-neutral fact delivered to Payments: "a PIX charge was
/// confirmed" (Fase 10, Checkpoint 5 — PIX/Payment Deterministic
/// Foundation). Mirrors <c>CreateCleaningForReservation</c>/<c>CloseReservation</c>'s
/// own shape and placement — a cross-context message living in the
/// RECEIVING context's own Contracts project, not an
/// <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>
/// (it is not something Payments itself publishes about its own past; it is
/// an inbound fact from outside).
///
/// This checkpoint has no real PIX provider and no real webhook (explicit
/// scope boundary — Fase 10, CP5 mandate items 36/37): NOTHING in
/// Production code publishes this message yet. It exists as the
/// provider-neutral seam a FUTURE ExternalIntegrations webhook-normalization
/// step is expected to produce — the Payments-side handler is genuine,
/// approved production code, never test-only scaffolding. Until that future
/// provider exists, the only publisher is the E2E test harness itself,
/// simulating the fact deterministically via a real Wolverine/RabbitMQ send
/// (never a test-only HTTP endpoint, never test logic embedded in the
/// domain — see <c>PixChargeConfirmationReceivedHandler</c>).
///
/// Payload is provider-neutral and deliberately minimal: no provider DTO, no
/// QR/copy-paste payload, no payer PII, no provider secret. <c>PixChargeId</c>
/// (not a provider-specific charge id) is sufficient to correlate — this
/// checkpoint's lifecycle does not require providerChargeId-based routing;
/// a future real provider integration may need to add that separately.
/// </summary>
public sealed record PixChargeConfirmationReceived
{
    public required Guid TenantId { get; init; }

    public required Guid PixChargeId { get; init; }

    public required DateTimeOffset ConfirmedAtUtc { get; init; }

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }
}
