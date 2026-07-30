using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="ChangeOwnPasswordCommand"/>
/// (Incremento 3, Checkpoint 9) — mirrors <c>UpdateUserTenantAwareBehavior</c>
/// exactly. Registered as a CLOSED generic
/// (<c>IPipelineBehavior&lt;ChangeOwnPasswordCommand, Result&gt;</c>), never an
/// open generic (Architecture Principles §6).
/// </summary>
public sealed class ChangeOwnPasswordTenantAwareBehavior : IPipelineBehavior<ChangeOwnPasswordCommand, Result>
{
    private readonly IChangeOwnPasswordExecutor _executor;

    public ChangeOwnPasswordTenantAwareBehavior(IChangeOwnPasswordExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        ChangeOwnPasswordCommand message, MessageHandlerDelegate<ChangeOwnPasswordCommand, Result> next, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
