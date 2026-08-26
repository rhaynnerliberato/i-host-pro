using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Infrastructure.Messaging;

/// <inheritdoc cref="IReservationsMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Reservations authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-016 rationale. Opens a fresh Microsoft DI child scope
/// per message (rooted at the application's own root provider — a
/// <see cref="IServiceScopeFactory"/> resolved from ANY scope, including
/// Wolverine's own per-message scope, always creates children off the root
/// container, never off "the current scope"), resolves
/// <see cref="ITenantContext"/> from THAT scope and resolves it to
/// <c>tenantId</c> BEFORE resolving the business processor —
/// <c>ReservationsDbContext</c>/the transaction executor, resolved later
/// from the SAME scope via ordinary constructor injection, therefore
/// observe the SAME resolved <see cref="ITenantContext"/> instance, entirely
/// outside Wolverine's own per-message DI resolution (which is where the
/// divergence was traced to — see ADR-016).
///
/// Resolves <see cref="IIntegrationEventHandler{TMessage}"/> via
/// <see cref="HandlerKey"/> (Fase 7, Incremento 2, Checkpoint 1 — real-Worker
/// regression found and fixed): that interface is shared across Bounded
/// Contexts, and once Dashboard also registers handlers for the same ten
/// Cleaning lifecycle event types this scope consumes, an unkeyed
/// <c>GetRequiredService</c> would silently resolve whichever registration
/// was added LAST across the whole composition root — not necessarily
/// Reservations' own.
/// </remarks>
public sealed class ReservationsMessageExecutionScope : IReservationsMessageExecutionScope
{
    /// <summary>Keyed-DI key every Reservations <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "reservations";

    private readonly IServiceScopeFactory _scopeFactory;

    public ReservationsMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

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

    /// <inheritdoc cref="IReservationsMessageExecutionScope.ExecuteCloseReservationAsync"/>
    /// <remarks>
    /// Resolves <see cref="ICloseReservationHandler"/> unkeyed — unlike
    /// <see cref="IIntegrationEventHandler{TMessage}"/>, it is exclusive to
    /// Reservations, registered once, with no other context competing for
    /// the same generic slot (mirrors
    /// <c>HousekeepingMessageExecutionScope.ExecuteCreateCleaningForReservationAsync</c>).
    /// </remarks>
    public async Task ExecuteCloseReservationAsync(
        CloseReservation command, Guid messageId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(command.TenantId);

        var handler = scope.ServiceProvider.GetRequiredService<ICloseReservationHandler>();
        await handler.HandleAsync(command, cancellationToken);
    }
}
