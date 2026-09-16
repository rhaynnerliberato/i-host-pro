using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>No real transaction — just runs the operation, since these fakes have no database to commit against.</summary>
internal sealed class FakeAirbnbEmailUnitOfWork : IAirbnbEmailUnitOfWork
{
    public int ExecutionCount { get; private set; }

    public async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return await operation();
    }
}
