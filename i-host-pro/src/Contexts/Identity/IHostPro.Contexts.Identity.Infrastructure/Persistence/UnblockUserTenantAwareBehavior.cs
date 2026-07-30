using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="UnblockUserCommand"/> (Incremento
/// 3, Checkpoint 7). Unlike every other state-mutating command in this
/// module, this one delegates directly to the shared, generic
/// <see cref="IIdentityTransactionExecutor"/> — no command-specific executor,
/// no bounded concurrency retry, no post-commit
/// <see cref="ISessionRevocationCache"/> write. Mirrors
/// <see cref="LoginTenantAwareBehavior"/>'s exact same "outbox needed, retry
/// not" shape (see <see cref="UnblockUserCommandHandler"/>'s doc comment for
/// why no genuine <c>DbUpdateConcurrencyException</c> risk exists here),
/// except this command's tenant is already resolved from the authenticated
/// Administrator's JWT claim by the time the Mediator pipeline runs — no
/// <c>ITenantBootstrapResolver</c> needed, exactly like
/// <see cref="AssignRoleTenantAwareBehavior"/>.
/// </summary>
public sealed class UnblockUserTenantAwareBehavior : IPipelineBehavior<UnblockUserCommand, Result>
{
    private readonly IIdentityTransactionExecutor _executor;

    public UnblockUserTenantAwareBehavior(IIdentityTransactionExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        UnblockUserCommand message, MessageHandlerDelegate<UnblockUserCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
