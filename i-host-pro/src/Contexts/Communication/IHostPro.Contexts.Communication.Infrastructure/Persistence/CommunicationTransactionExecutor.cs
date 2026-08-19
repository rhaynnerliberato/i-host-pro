using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Application;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

/// <inheritdoc cref="ICommunicationTransactionExecutor"/>
public sealed class CommunicationTransactionExecutor : ICommunicationTransactionExecutor
{
    private readonly CommunicationDbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public CommunicationTransactionExecutor(CommunicationDbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly: false, cancellationToken);

        var result = await operation();
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
