using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="LoginCommand"/> (Incremento 2 plan,
/// Etapa 15A — replaces the shared, generic <c>TenantBootstrapBehavior&lt;,&gt;</c>
/// this command used through Etapa 14): resolves the tenant from the request
/// itself (<see cref="ITenantBootstrapResolver{TRequest}"/>, same mechanism
/// <c>TenantBootstrapBehavior</c> used), then runs the rest of the pipeline
/// through <see cref="IIdentityTransactionExecutor"/> instead of the plain
/// <c>ITenantAwareUnitOfWork</c> — so a rejected login can still atomically
/// persist a lockout increment, an audit entry and any staged Integration
/// Event to Identity's durable outbox, exactly like Refresh/Logout.
///
/// Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;LoginCommand, Result&lt;AuthTokensResult&gt;&gt;</c>),
/// mirroring <see cref="RefreshTokenTenantAwareBehavior"/>/<see cref="LogoutTenantAwareBehavior"/> —
/// all three auth commands now delegate to the same
/// <see cref="IIdentityTransactionExecutor"/>, never to the generic
/// <c>TenantBootstrapBehavior&lt;,&gt;</c>/<c>TenantTransactionBehavior&lt;,&gt;</c>.
/// </summary>
public sealed class LoginTenantAwareBehavior : IPipelineBehavior<LoginCommand, Result<AuthTokensResult>>
{
    private readonly ITenantBootstrapResolver<LoginCommand> _tenantResolver;
    private readonly ITenantContext _tenantContext;
    private readonly IIdentityTransactionExecutor _executor;

    public LoginTenantAwareBehavior(
        ITenantBootstrapResolver<LoginCommand> tenantResolver,
        ITenantContext tenantContext,
        IIdentityTransactionExecutor executor)
    {
        _tenantResolver = tenantResolver;
        _tenantContext = tenantContext;
        _executor = executor;
    }

    public async ValueTask<Result<AuthTokensResult>> Handle(
        LoginCommand message,
        MessageHandlerDelegate<LoginCommand, Result<AuthTokensResult>> next,
        CancellationToken cancellationToken)
    {
        var tenantId = await _tenantResolver.ResolveTenantAsync(message, cancellationToken);
        if (tenantId is null)
            return Result.Failure<AuthTokensResult>(new Error("Tenant.NotFound", "The tenant could not be resolved."));

        _tenantContext.SetTenant(tenantId.Value);

        return await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
    }
}
