using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="RefreshTokenCommand"/>
/// (Incremento 2 plan, Etapa 14): resolves the tenant from the token itself
/// (<see cref="ITenantBootstrapResolver{TRequest}"/>, mirroring what
/// <c>TenantBootstrapBehavior</c> does for other bootstrap requests), then
/// runs the rest of the pipeline through <see cref="IRefreshTokenExchangeExecutor"/>
/// instead of the plain <c>ITenantAwareUnitOfWork</c> directly — the
/// Refresh-specific bounded concurrency retry (Etapa 10) must be on the real
/// production dispatch path, not only exercised by tests replicating it
/// manually.
///
/// Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;RefreshTokenCommand, Result&lt;AuthTokensResult&gt;&gt;</c>),
/// never as an open generic alongside the shared, generic
/// <c>TenantBootstrapBehavior&lt;,&gt;</c> — both would otherwise match
/// <see cref="RefreshTokenCommand"/> (it implements <c>IBootstrapRequest</c>)
/// and each independently try to open a Unit of Work, causing a
/// <c>NestedUnitOfWorkException</c>.
/// </summary>
public sealed class RefreshTokenTenantAwareBehavior : IPipelineBehavior<RefreshTokenCommand, Result<AuthTokensResult>>
{
    private readonly ITenantBootstrapResolver<RefreshTokenCommand> _tenantResolver;
    private readonly ITenantContext _tenantContext;
    private readonly IRefreshTokenExchangeExecutor _executor;

    public RefreshTokenTenantAwareBehavior(
        ITenantBootstrapResolver<RefreshTokenCommand> tenantResolver,
        ITenantContext tenantContext,
        IRefreshTokenExchangeExecutor executor)
    {
        _tenantResolver = tenantResolver;
        _tenantContext = tenantContext;
        _executor = executor;
    }

    public async ValueTask<Result<AuthTokensResult>> Handle(
        RefreshTokenCommand message,
        MessageHandlerDelegate<RefreshTokenCommand, Result<AuthTokensResult>> next,
        CancellationToken cancellationToken)
    {
        var tenantId = await _tenantResolver.ResolveTenantAsync(message, cancellationToken);
        if (tenantId is null)
            return Result.Failure<AuthTokensResult>(new Error("Tenant.NotFound", "The tenant could not be resolved."));

        _tenantContext.SetTenant(tenantId.Value);

        return await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
    }
}
