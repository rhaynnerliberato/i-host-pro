using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Runs the operation directly — no real EF/concurrency-exception translation needed for fast unit tests (mirrors <c>PassThroughExternalIntegrationsTransactionExecutor</c>).</summary>
internal sealed class PassThroughRetryAirbnbEmailMessageReceiptExecutor : IRetryAirbnbEmailMessageReceiptExecutor
{
    public Task<Result<AirbnbEmailMessageReceiptResult>> ExecuteAsync(
        Func<Task<Result<AirbnbEmailMessageReceiptResult>>> operation, CancellationToken cancellationToken) =>
        operation();
}
