using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Wraps the retry command's own mutation/publish transaction, translating a
/// caught <c>DbUpdateConcurrencyException</c> into
/// <see cref="AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict"/> — mirrors
/// <c>Reservations.Application.Reservations.IUpdateReservationExecutor</c>'s
/// own concurrency-only translation, kept out of the Application layer
/// itself (which must never reference EF Core directly).
/// </summary>
public interface IRetryAirbnbEmailMessageReceiptExecutor
{
    Task<Result<AirbnbEmailMessageReceiptResult>> ExecuteAsync(
        Func<Task<Result<AirbnbEmailMessageReceiptResult>>> operation, CancellationToken cancellationToken);
}
