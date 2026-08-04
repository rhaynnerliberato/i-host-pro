using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step for <see cref="CreatePropertyCommand"/>
/// (Checkpoint 3 plan) — mirrors <c>CreateCondominiumTenantAwareBehavior</c>,
/// but delegates to <see cref="ICreatePropertyExecutor"/> (unique-code
/// translation) instead of the shared transaction executor directly, since
/// unlike Condominium, Property has a uniqueness constraint on creation.
/// Registered as a CLOSED generic, never an open generic.
/// </summary>
public sealed class CreatePropertyTenantAwareBehavior : IPipelineBehavior<CreatePropertyCommand, Result<PropertyResult>>
{
    private readonly ICreatePropertyExecutor _executor;

    public CreatePropertyTenantAwareBehavior(ICreatePropertyExecutor executor) => _executor = executor;

    public async ValueTask<Result<PropertyResult>> Handle(
        CreatePropertyCommand message,
        MessageHandlerDelegate<CreatePropertyCommand, Result<PropertyResult>> next,
        CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
