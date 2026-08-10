using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Infrastructure.Messaging;

/// <summary>
/// The minimal, mechanical adapter the messaging library (Wolverine)
/// discovers by its own naming convention (class name ending in
/// <c>Handler</c>, public static method named <c>Handle</c>) and invokes for
/// every consumed <c>PolicyUpdated</c> message (Architecture Principles §11)
/// — contains no business logic itself, only resolves and delegates to
/// <see cref="IIntegrationEventHandler{TEvent}"/>, whose implementation
/// (<see cref="PolicyUpdatedCacheInvalidation"/>) never references
/// Wolverine at all. <c>IIntegrationEventHandler&lt;PolicyUpdated&gt;</c> and
/// <see cref="CancellationToken"/> are both resolved by Wolverine's own
/// method-injection convention — no other wiring is needed for this class to
/// be discovered, once <c>IHostPro.Worker</c>'s composition root includes
/// this assembly in Wolverine's handler discovery
/// (<c>opts.Discovery.IncludeAssembly</c>).
/// </summary>
public static class PolicyUpdatedHandler
{
    public static Task Handle(
        PolicyUpdated message, IIntegrationEventHandler<PolicyUpdated> handler, CancellationToken cancellationToken) =>
        handler.HandleAsync(message, cancellationToken);
}
