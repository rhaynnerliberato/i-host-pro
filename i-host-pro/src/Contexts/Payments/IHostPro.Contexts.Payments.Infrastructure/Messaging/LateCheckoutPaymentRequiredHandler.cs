using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Payments.Application;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Payments.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="LateCheckoutPaymentRequired"/> (Fase 10,
/// Checkpoint 5 — PIX/Payment Deterministic Foundation; choreography, async
/// boundary — approved decision, no synchronous GuestOperations → Payments
/// call). Depends ONLY on <see cref="IPaymentsMessageExecutionScope"/> and
/// Wolverine's own <see cref="MessageContext"/> — never on
/// <c>PaymentsDbContext</c> or <c>LateCheckoutPaymentRequiredChargeInitializer</c>
/// directly.
/// </summary>
[NonTransactional]
public static class LateCheckoutPaymentRequiredHandler
{
    public static Task Handle(
        LateCheckoutPaymentRequired message,
        MessageContext context,
        IPaymentsMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
