using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="CreateUserCommand"/> (Incremento
/// 3, Checkpoint 5) — mirrors <c>LogoutTenantAwareBehavior</c>/
/// <c>RevokeOwnSessionTenantAwareBehavior</c> exactly. Not an
/// <c>IBootstrapRequest</c>: the caller (an Administrator) is already
/// authenticated, so the tenant is already set on <c>ITenantContext</c> by
/// the JWT Bearer authentication event before this pipeline runs.
///
/// Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;CreateUserCommand, Result&lt;UserResult&gt;&gt;</c>),
/// never an open generic — this replaces the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> for CreateUser specifically, so
/// its outbox publication and unique-violation translation
/// (<see cref="ICreateUserExecutor"/>) are on the real production dispatch
/// path.
/// </summary>
public sealed class CreateUserTenantAwareBehavior : IPipelineBehavior<CreateUserCommand, Result<UserResult>>
{
    private readonly ICreateUserExecutor _executor;

    public CreateUserTenantAwareBehavior(ICreateUserExecutor executor) => _executor = executor;

    public async ValueTask<Result<UserResult>> Handle(
        CreateUserCommand message, MessageHandlerDelegate<CreateUserCommand, Result<UserResult>> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
