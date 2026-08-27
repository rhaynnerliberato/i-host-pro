using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="GuestCheckedOut"/> (Fase 10, Checkpoint
/// 1 — Guest Operations Foundation) — mirrors <see cref="ReservationCreatedHandler"/>'s
/// own shape exactly, including the reasons for skipping any
/// <c>I&lt;Context&gt;MessageExecutionScope</c> indirection (this context is
/// stateless — no DbContext, approved Decision Material 4) and for resolving
/// the keyed <see cref="IIntegrationEventHandler{TEvent}"/> registration via
/// an ordinary CONSTRUCTOR-injected <c>[FromKeyedServices]</c> parameter.
/// </summary>
[NonTransactional]
public sealed class GuestCheckedOutHandler
{
    private readonly IIntegrationEventHandler<GuestCheckedOut> _handler;

    public GuestCheckedOutHandler(
        [FromKeyedServices(WorkflowModuleExtensions.HandlerKey)] IIntegrationEventHandler<GuestCheckedOut> handler) =>
        _handler = handler;

    public Task Handle(GuestCheckedOut message, CancellationToken cancellationToken) =>
        _handler.HandleAsync(message, cancellationToken);
}
