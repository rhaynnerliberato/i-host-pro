using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;

namespace IHostPro.Contexts.Workflow.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ReservationCreated</c> (Fase 8, Checkpoint 1 —
/// ADR-018). Unlike every other context's own Wolverine adapter in this
/// solution, this one does NOT go through an
/// <c>I&lt;Context&gt;MessageExecutionScope</c> indirection — that pattern
/// (ADR-015/016) exists solely to isolate a tenant-scoped
/// <c>DbContext</c>'s resolution of <c>ITenantContext</c> from Wolverine's
/// own per-message DI graph; this context has no <c>DbContext</c> at all
/// (approved stateless design, Decision Material 4), so there is nothing
/// for that mechanism to protect.
///
/// Resolves the keyed <see cref="IIntegrationEventHandler{TEvent}"/>
/// registration through an ordinary CONSTRUCTOR-injected
/// <c>[FromKeyedServices]</c> parameter — never a manual
/// <c>IServiceProvider.GetRequiredKeyedService</c> call inside the
/// <c>Handle</c> method itself. A real-Worker run proved the latter fails
/// Wolverine's strict codegen with <c>InvalidServiceLocationException</c>
/// ("Service System.IServiceProvider: Directly using scoped
/// IServiceProvider") — Wolverine's codegen only inlines dependencies it
/// can resolve as ordinary constructor parameters (keyed or not); a raw
/// <c>IServiceProvider</c> parameter is treated as unverified manual
/// service location, same class of restriction ADR-015/016 already
/// document for <c>IServiceScopeFactory</c>. Keyed (not a plain dependency)
/// because <c>ReservationCreated</c> already has other consumers
/// (Housekeeping, Dashboard) in the same <c>IHostPro.Worker</c> process,
/// and an unkeyed resolution would non-deterministically resolve whichever
/// registration was added last across the whole composition root (Fase
/// 7's own real-Worker regression, already fixed there via keying —
/// avoided here from the start).
/// </summary>
[NonTransactional]
public sealed class ReservationCreatedHandler
{
    private readonly IIntegrationEventHandler<ReservationCreated> _handler;

    public ReservationCreatedHandler(
        [FromKeyedServices(WorkflowModuleExtensions.HandlerKey)] IIntegrationEventHandler<ReservationCreated> handler) =>
        _handler = handler;

    public Task Handle(ReservationCreated message, CancellationToken cancellationToken) =>
        _handler.HandleAsync(message, cancellationToken);
}
