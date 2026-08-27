using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <see cref="LateCheckoutApproved"/> (Fase 10,
/// Checkpoint 3 — Early Check-in / Late Checkout) — mirrors
/// <see cref="GuestCheckedOutHandler"/>'s own shape exactly. Keyed
/// registration is mandatory here even before Housekeeping's own separate
/// consumer of the same event exists in this process — see
/// <see cref="WorkflowModuleExtensions"/>' own registration comment
/// (ADR-020).
/// </summary>
[NonTransactional]
public sealed class LateCheckoutApprovedHandler
{
    private readonly IIntegrationEventHandler<LateCheckoutApproved> _handler;

    public LateCheckoutApprovedHandler(
        [FromKeyedServices(WorkflowModuleExtensions.HandlerKey)] IIntegrationEventHandler<LateCheckoutApproved> handler) =>
        _handler = handler;

    public Task Handle(LateCheckoutApproved message, CancellationToken cancellationToken) =>
        _handler.HandleAsync(message, cancellationToken);
}
