using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Dashboard.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Messaging;

/// <inheritdoc cref="IDashboardMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Dashboard authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-016 rationale. Opens a fresh Microsoft DI child scope
/// per message (rooted at the application's own root provider), resolves
/// <see cref="ITenantContext"/> from THAT scope and resolves it to
/// <c>tenantId</c> BEFORE resolving the business processor —
/// <c>DashboardDbContext</c>/the transaction executor, resolved later from
/// the SAME scope via ordinary constructor injection, therefore observe the
/// SAME resolved <see cref="ITenantContext"/> instance, entirely outside
/// Wolverine's own per-message DI resolution — mirrors
/// <c>HousekeepingMessageExecutionScope</c>/<c>ReservationsMessageExecutionScope</c>
/// exactly.
///
/// Resolves <see cref="IIntegrationEventHandler{TMessage}"/> via
/// <see cref="HandlerKey"/> (Fase 7, Incremento 2, Checkpoint 1 — real-Worker
/// regression found and fixed): <c>IIntegrationEventHandler&lt;T&gt;</c> is
/// shared across Bounded Contexts, and most of the event types Dashboard
/// consumes (PropertyCreated/PropertyActivated/PropertyDeactivated/
/// PropertyArchived/ReservationCreated/ReservationCancelled/all ten Cleaning
/// lifecycle events) are ALSO already consumed by Housekeeping and/or
/// Reservations in the same <c>IHostPro.Worker</c> DI container. An unkeyed
/// <c>GetRequiredService</c> would silently resolve whichever registration
/// was added LAST across the whole composition root — not necessarily
/// Dashboard's own — and, worse, would shadow the OTHER contexts' own
/// resolution too, since they would also be calling the same unkeyed
/// method. Keying every registration by owning context makes each scope's
/// resolution unambiguous regardless of registration order.
/// </remarks>
public sealed class DashboardMessageExecutionScope : IDashboardMessageExecutionScope
{
    /// <summary>Keyed-DI key every Dashboard <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "dashboard";

    private readonly IServiceScopeFactory _scopeFactory;

    public DashboardMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var processor = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventHandler<TMessage>>(HandlerKey);
        await processor.HandleAsync(message, cancellationToken);
    }
}
