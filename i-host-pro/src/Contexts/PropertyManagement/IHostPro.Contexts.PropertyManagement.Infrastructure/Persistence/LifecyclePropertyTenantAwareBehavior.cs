using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// The Mediator pipeline step shared by the three lifecycle commands
/// (<c>ActivatePropertyCommand</c>/<c>DeactivatePropertyCommand</c>/
/// <c>ArchivePropertyCommand</c> — Checkpoint 4 plan, item 12), delegating to
/// <see cref="ILifecyclePropertyExecutor"/> (concurrency translation only).
/// A generic CLASS reused across the three, but registered as three separate
/// CLOSED-generic pipeline behaviors (never the open generic
/// <c>typeof(IPipelineBehavior&lt;,&gt;)</c>) — mirrors
/// <c>TenantTransactionBehavior&lt;TMessage,TResponse&gt;</c>'s own
/// established shape (Architecture Principles §6: closed-per-message-type
/// registration, generic implementation is fine).
/// </summary>
public sealed class LifecyclePropertyTenantAwareBehavior<TCommand> : IPipelineBehavior<TCommand, Result<PropertyResult>>
    where TCommand : IMessage
{
    private readonly ILifecyclePropertyExecutor _executor;

    public LifecyclePropertyTenantAwareBehavior(ILifecyclePropertyExecutor executor) => _executor = executor;

    public async ValueTask<Result<PropertyResult>> Handle(
        TCommand message,
        MessageHandlerDelegate<TCommand, Result<PropertyResult>> next,
        CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(() => next(message, cancellationToken).AsTask(), cancellationToken);
}
