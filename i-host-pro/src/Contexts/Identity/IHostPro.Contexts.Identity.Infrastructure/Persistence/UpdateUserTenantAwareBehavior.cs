using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="UpdateUserCommand"/> (Incremento
/// 3, Checkpoint 8) — mirrors <c>CreateUserTenantAwareBehavior</c> exactly.
/// Not an <c>IBootstrapRequest</c>: the caller (an Administrator) is already
/// authenticated, so the tenant is already set on <c>ITenantContext</c> by
/// the JWT Bearer authentication event before this pipeline runs.
///
/// Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;UpdateUserCommand, Result&lt;UserResult&gt;&gt;</c>),
/// never an open generic — this replaces the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> for UpdateUser specifically, so
/// its outbox publication and exception translation
/// (<see cref="IUpdateUserExecutor"/>) are on the real production dispatch
/// path.
/// </summary>
public sealed class UpdateUserTenantAwareBehavior : IPipelineBehavior<UpdateUserCommand, Result<UserResult>>
{
    private readonly IUpdateUserExecutor _executor;

    public UpdateUserTenantAwareBehavior(IUpdateUserExecutor executor) => _executor = executor;

    public async ValueTask<Result<UserResult>> Handle(
        UpdateUserCommand message, MessageHandlerDelegate<UpdateUserCommand, Result<UserResult>> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
