using IHostPro.Contexts.Payments.Contracts;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Handles <see cref="PixChargeConfirmationReceived"/> (Fase 10, Checkpoint
/// 5 — PIX/Payment Deterministic Foundation) — mirrors
/// <c>Housekeeping.Application.Cleanings.ICreateCleaningForReservationHandler</c>'s
/// own reasoning exactly: deliberately not modeled through the Mediator
/// <c>ICommandHandler&lt;,&gt;</c> pipeline (Payments has no HTTP-facing
/// commands at all this checkpoint), consumed exclusively in
/// <c>IHostPro.Worker</c>.
/// </summary>
public interface IPixChargeConfirmationReceivedHandler
{
    Task HandleAsync(PixChargeConfirmationReceived message, CancellationToken cancellationToken);
}
