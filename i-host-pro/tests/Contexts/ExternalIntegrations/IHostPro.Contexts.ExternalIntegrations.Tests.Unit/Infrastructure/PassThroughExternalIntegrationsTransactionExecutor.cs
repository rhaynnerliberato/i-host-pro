using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>
/// Runs the operation directly — no real transaction/outbox needed for fast
/// unit tests. The real transaction/outbox semantics (including the atomic
/// commit of a receipt mutation together with the outbox envelope this
/// executor stands in for) are covered by
/// <c>AirbnbAutoPublishNestedTransactionRegressionTests</c> in the
/// real-Postgres/real-RabbitMq Integration suite — confirmed by that test
/// actually existing and exercising the real
/// <c>ExternalIntegrationsOutboxTransactionExecutor</c>, not merely assumed.
/// </summary>
internal sealed class PassThroughExternalIntegrationsTransactionExecutor : IExternalIntegrationsTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}
