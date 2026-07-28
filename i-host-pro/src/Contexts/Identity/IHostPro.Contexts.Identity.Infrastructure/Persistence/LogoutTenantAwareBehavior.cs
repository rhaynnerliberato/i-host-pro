using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="LogoutCommand"/> (Incremento 2
/// plan, Etapa 14). Unlike Login/Refresh, <see cref="LogoutCommand"/> is not
/// an <c>IBootstrapRequest</c> — the tenant is already set on
/// <c>ITenantContext</c> by the JWT Bearer authentication event before this
/// pipeline runs, so there is no tenant-resolution step here. This just runs
/// the rest of the pipeline through <see cref="ILogoutExecutor"/> instead of
/// the plain <c>ITenantAwareUnitOfWork</c> directly — <c>ILogoutExecutor</c>
/// itself calls into the Unit of Work and throws
/// <c>TenantContextNotResolvedException</c> if the tenant was somehow never
/// resolved, so no separate check is needed here either.
///
/// Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;LogoutCommand, Result&gt;</c>), not as an open
/// generic alongside the shared, generic <c>TenantTransactionBehavior&lt;,&gt;</c>
/// — this replaces it for Logout specifically (Etapa 11's bounded
/// concurrency retry must be on the real production dispatch path).
/// </summary>
public sealed class LogoutTenantAwareBehavior : IPipelineBehavior<LogoutCommand, Result>
{
    private readonly ILogoutExecutor _executor;

    public LogoutTenantAwareBehavior(ILogoutExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        LogoutCommand message, MessageHandlerDelegate<LogoutCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
