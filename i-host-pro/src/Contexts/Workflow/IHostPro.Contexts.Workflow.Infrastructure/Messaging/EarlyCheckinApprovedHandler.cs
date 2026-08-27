using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="EarlyCheckinApproved"/> (Fase 10,
/// Checkpoint 3 — Early Check-in / Late Checkout) — mirrors
/// <see cref="GuestCheckedOutHandler"/>'s own shape exactly.
/// </summary>
[NonTransactional]
public sealed class EarlyCheckinApprovedHandler
{
    private readonly IIntegrationEventHandler<EarlyCheckinApproved> _handler;

    public EarlyCheckinApprovedHandler(
        [FromKeyedServices(WorkflowModuleExtensions.HandlerKey)] IIntegrationEventHandler<EarlyCheckinApproved> handler) =>
        _handler = handler;

    public Task Handle(EarlyCheckinApproved message, CancellationToken cancellationToken) =>
        _handler.HandleAsync(message, cancellationToken);
}
