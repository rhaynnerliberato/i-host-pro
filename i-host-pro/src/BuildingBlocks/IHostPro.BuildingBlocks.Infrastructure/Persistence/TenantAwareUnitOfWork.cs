using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <inheritdoc cref="ITenantAwareUnitOfWork"/>
/// <remarks>
/// Parameterized by the specific <typeparamref name="TDbContext"/> it must
/// open its transaction against — never the base <see cref="DbContext"/>
/// type. Every Bounded Context registers its own <see cref="DbContext"/>
/// subclass (<c>IdentityDbContext</c>, <c>PropertyManagementDbContext</c>,
/// <c>ReservationsDbContext</c>); resolving a bare, unparameterized
/// <see cref="DbContext"/> from the container is ambiguous the moment more
/// than one is registered — whichever module's <c>AddDbContext&lt;T&gt;</c>
/// ran last silently wins for every caller, including Bounded Contexts other
/// than its own. This is what broke <c>GetOwnProfileQuery</c> (Fase 4
/// homologation): its <see cref="TenantTransactionBehavior{TMessage,TResponse,TDbContext}"/>
/// opened the RLS transaction (<c>SET LOCAL app.tenant_id</c>) against
/// whichever concrete <see cref="DbContext"/> the container happened to
/// resolve for the ambiguous base type, while the actual query ran against
/// <c>IdentityDbContext</c> on a different connection with no tenant set —
/// PostgreSQL's Row-Level Security then silently returned zero rows. Being
/// generic here makes that class of collision impossible: DI resolves this
/// type per closed <typeparamref name="TDbContext"/>, never ambiguously.
/// </remarks>
public sealed class TenantAwareUnitOfWork<TDbContext> : ITenantAwareUnitOfWork
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public TenantAwareUnitOfWork(TDbContext dbContext, ITenantContext tenantContext)
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
