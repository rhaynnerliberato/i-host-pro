using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="RemoveRoleCommand"/> (Incremento
/// 3, Checkpoint 6) — mirrors <see cref="AssignRoleTenantAwareBehavior"/>
/// exactly.
/// </summary>
public sealed class RemoveRoleTenantAwareBehavior : IPipelineBehavior<RemoveRoleCommand, Result>
{
    private readonly IRemoveRoleExecutor _executor;

    public RemoveRoleTenantAwareBehavior(IRemoveRoleExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        RemoveRoleCommand message, MessageHandlerDelegate<RemoveRoleCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
