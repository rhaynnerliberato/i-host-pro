using IHostPro.Contexts.Payments.Contracts;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Handles <see cref="PixChargeExpirationReceived"/> (Fase 10, Checkpoint
/// 5.1 — Payment Failure/Expiration Evidence Corrective Gate) — mirrors
/// <see cref="IPixChargeConfirmationReceivedHandler"/>'s own reasoning
/// exactly: deliberately not modeled through the Mediator
/// <c>ICommandHandler&lt;,&gt;</c> pipeline, consumed exclusively in
/// <c>IHostPro.Worker</c>.
/// </summary>
public interface IPixChargeExpirationReceivedHandler
{
    Task HandleAsync(PixChargeExpirationReceived message, CancellationToken cancellationToken);
}
