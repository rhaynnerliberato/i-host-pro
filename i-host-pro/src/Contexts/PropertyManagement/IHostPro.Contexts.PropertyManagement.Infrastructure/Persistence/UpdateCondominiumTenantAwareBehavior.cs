using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="UpdateCondominiumCommand"/>
/// (Checkpoint 2 plan) — mirrors <c>CreateCondominiumTenantAwareBehavior</c>,
/// but delegates to <see cref="IUpdateCondominiumExecutor"/> (concurrency
/// translation) instead of the shared transaction executor directly.
/// Registered as a CLOSED generic, never an open generic.
/// </summary>
public sealed class UpdateCondominiumTenantAwareBehavior : IPipelineBehavior<UpdateCondominiumCommand, Result<CondominiumResult>>
{
    private readonly IUpdateCondominiumExecutor _executor;

    public UpdateCondominiumTenantAwareBehavior(IUpdateCondominiumExecutor executor) => _executor = executor;

    public async ValueTask<Result<CondominiumResult>> Handle(
        UpdateCondominiumCommand message,
        MessageHandlerDelegate<UpdateCondominiumCommand, Result<CondominiumResult>> next,
        CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
