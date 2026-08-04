using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="UpdatePropertyCommand"/>
/// (Checkpoint 3 plan) — mirrors <c>UpdateCondominiumTenantAwareBehavior</c>,
/// delegating to <see cref="IUpdatePropertyExecutor"/> (uniqueness +
/// concurrency translation). Registered as a CLOSED generic, never an open
/// generic.
/// </summary>
public sealed class UpdatePropertyTenantAwareBehavior : IPipelineBehavior<UpdatePropertyCommand, Result<PropertyResult>>
{
    private readonly IUpdatePropertyExecutor _executor;

    public UpdatePropertyTenantAwareBehavior(IUpdatePropertyExecutor executor) => _executor = executor;

    public async ValueTask<Result<PropertyResult>> Handle(
        UpdatePropertyCommand message,
        MessageHandlerDelegate<UpdatePropertyCommand, Result<PropertyResult>> next,
        CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
