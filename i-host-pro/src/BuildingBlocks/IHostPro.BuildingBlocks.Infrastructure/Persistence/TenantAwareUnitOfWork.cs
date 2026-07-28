using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <inheritdoc cref="ITenantAwareUnitOfWork"/>
public sealed class TenantAwareUnitOfWork : ITenantAwareUnitOfWork
{
    private readonly DbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public TenantAwareUnitOfWork(DbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> ExecuteAsync<TResponse>(
        bool readOnly,
        Func<Task<TResponse>> operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly, cancellationToken);

        var result = await operation();
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return result;
    }
}
