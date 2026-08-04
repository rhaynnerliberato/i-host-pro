using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="UnlinkPropertyOwnerCommand"/>
/// (Checkpoint 5 plan) — mirrors <c>UpdatePropertyTenantAwareBehavior</c>,
/// delegating to <see cref="IUnlinkPropertyOwnerExecutor"/> instead of the
/// shared transaction executor directly. Unlike <c>LinkPropertyOwnerCommand</c>
/// (which has no such wrapping behavior at all — see
/// <see cref="ILinkPropertyOwnerExecutor"/>'s own doc comment), Unlink's
/// entire operation legitimately runs inside Property Management's write
/// transaction, since it never calls Identity. Registered as a CLOSED
/// generic, never an open generic.
/// </summary>
public sealed class UnlinkPropertyOwnerTenantAwareBehavior : IPipelineBehavior<UnlinkPropertyOwnerCommand, Result>
{
    private readonly IUnlinkPropertyOwnerExecutor _executor;

    public UnlinkPropertyOwnerTenantAwareBehavior(IUnlinkPropertyOwnerExecutor executor) => _executor = executor;

    public async ValueTask<Result> Handle(
        UnlinkPropertyOwnerCommand message,
        MessageHandlerDelegate<UnlinkPropertyOwnerCommand, Result> next,
        CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
