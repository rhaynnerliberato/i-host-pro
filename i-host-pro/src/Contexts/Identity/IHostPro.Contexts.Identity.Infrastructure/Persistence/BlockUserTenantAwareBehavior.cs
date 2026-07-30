using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="BlockUserCommand"/> (Incremento
/// 3, Checkpoint 7) — mirrors <see cref="AssignRoleTenantAwareBehavior"/>
/// exactly.
/// </summary>
public sealed class BlockUserTenantAwareBehavior : IPipelineBehavior<BlockUserCommand, Result>
{
    private readonly IBlockUserExecutor _executor;

    public BlockUserTenantAwareBehavior(IBlockUserExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        BlockUserCommand message, MessageHandlerDelegate<BlockUserCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
