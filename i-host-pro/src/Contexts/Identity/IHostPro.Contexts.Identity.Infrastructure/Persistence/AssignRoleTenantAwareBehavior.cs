using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="AssignRoleCommand"/> (Incremento
/// 3, Checkpoint 6) — mirrors <c>RevokeOwnSessionTenantAwareBehavior</c>
/// exactly. Not an <c>IBootstrapRequest</c>: the caller (an Administrator) is
/// already authenticated, so the tenant is already set on
/// <c>ITenantContext</c> by the JWT Bearer authentication event before this
/// pipeline runs.
///
/// Registered as a CLOSED generic (<c>IPipelineBehavior&lt;AssignRoleCommand, Result&gt;</c>),
/// never alongside the shared, generic <c>TenantTransactionBehavior&lt;,&gt;</c>
/// — this replaces it for AssignRole specifically, so the command's outbox
/// publication and bounded concurrency retry
/// (<see cref="IAssignRoleExecutor"/>) are on the real production dispatch
/// path.
/// </summary>
public sealed class AssignRoleTenantAwareBehavior : IPipelineBehavior<AssignRoleCommand, Result>
{
    private readonly IAssignRoleExecutor _executor;

    public AssignRoleTenantAwareBehavior(IAssignRoleExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        AssignRoleCommand message, MessageHandlerDelegate<AssignRoleCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
