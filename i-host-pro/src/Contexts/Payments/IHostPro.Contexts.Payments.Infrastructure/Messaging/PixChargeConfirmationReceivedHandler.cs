using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Payments.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="PixChargeConfirmationReceived"/> (Fase
/// 10, Checkpoint 5 — PIX/Payment Deterministic Foundation). This checkpoint
/// has no real PIX provider/webhook — the only publisher today is the E2E
/// test harness, simulating the provider-neutral fact deterministically via
/// a real Wolverine/RabbitMQ send (never a test-only HTTP endpoint). The
/// handler itself is genuine production code: it represents the seam a
/// FUTURE ExternalIntegrations webhook-normalization step is expected to
/// publish through, unchanged. Mirrors
/// <c>Housekeeping.Infrastructure.Messaging.CreateCleaningForReservationHandler</c>'s
/// own shape exactly.
/// </summary>
[NonTransactional]
public static class PixChargeConfirmationReceivedHandler
{
    public static Task Handle(
        PixChargeConfirmationReceived message,
        MessageContext context,
        IPaymentsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecutePixChargeConfirmationReceivedAsync(message, context.Envelope!.Id, cancellationToken);
}
