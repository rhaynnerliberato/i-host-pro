using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>Runs the operation directly — no real transaction/outbox needed for fast unit tests (covered by the real-Postgres Integration suite).</summary>
internal sealed class PassThroughExternalIntegrationsTransactionExecutor : IExternalIntegrationsTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}
