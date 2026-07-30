using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Sessions;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="RevokeOwnSessionCommand"/>
/// (Incremento 3, Checkpoint 4) — mirrors <c>LogoutTenantAwareBehavior</c>
/// exactly. Not an <c>IBootstrapRequest</c>: the caller is already
/// authenticated, so the tenant is already set on <c>ITenantContext</c> by the
/// JWT Bearer authentication event before this pipeline runs.
///
/// Registered as a CLOSED generic (<c>IPipelineBehavior&lt;RevokeOwnSessionCommand, Result&gt;</c>),
/// not as an open generic alongside the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> — this replaces it for
/// RevokeOwnSession specifically, the same way <c>LogoutTenantAwareBehavior</c>
/// replaces it for Logout, so the command's outbox publication and bounded
/// concurrency retry (<see cref="IRevokeOwnSessionExecutor"/>) are on the real
/// production dispatch path.
/// </summary>
public sealed class RevokeOwnSessionTenantAwareBehavior : IPipelineBehavior<RevokeOwnSessionCommand, Result>
{
    private readonly IRevokeOwnSessionExecutor _executor;

    public RevokeOwnSessionTenantAwareBehavior(IRevokeOwnSessionExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        RevokeOwnSessionCommand message, MessageHandlerDelegate<RevokeOwnSessionCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
