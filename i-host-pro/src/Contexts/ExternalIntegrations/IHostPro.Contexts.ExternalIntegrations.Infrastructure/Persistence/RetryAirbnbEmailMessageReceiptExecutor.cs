using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <inheritdoc cref="IRetryAirbnbEmailMessageReceiptExecutor"/>
public sealed class RetryAirbnbEmailMessageReceiptExecutor : IRetryAirbnbEmailMessageReceiptExecutor
{
    private static readonly Error ConflictError = new(AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict, AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict);

    private readonly IExternalIntegrationsTransactionExecutor _transactionExecutor;

    public RetryAirbnbEmailMessageReceiptExecutor(IExternalIntegrationsTransactionExecutor transactionExecutor) =>
        _transactionExecutor = transactionExecutor;

    public async Task<Result<AirbnbEmailMessageReceiptResult>> ExecuteAsync(
        Func<Task<Result<AirbnbEmailMessageReceiptResult>>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _transactionExecutor.ExecuteAsync(operation, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<AirbnbEmailMessageReceiptResult>(ConflictError);
        }
    }
}
