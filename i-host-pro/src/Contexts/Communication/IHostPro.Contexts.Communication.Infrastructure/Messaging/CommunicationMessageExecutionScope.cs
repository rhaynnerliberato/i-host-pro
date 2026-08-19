using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Communication.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <inheritdoc cref="ICommunicationMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Communication authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-016 rationale. Opens a fresh Microsoft DI child scope
/// per message, resolves <see cref="ITenantContext"/> from THAT scope and
/// sets it to <c>tenantId</c> BEFORE resolving the business processor —
/// mirrors <c>DashboardMessageExecutionScope</c>/<c>HousekeepingMessageExecutionScope</c>/
/// <c>ReservationsMessageExecutionScope</c> exactly.
///
/// Resolves <see cref="IIntegrationEventHandler{TMessage}"/> via
/// <see cref="HandlerKey"/> — <c>ReservationCreated</c> already has other
/// consumers (Housekeeping, Dashboard, Workflow) in the same
/// <c>IHostPro.Worker</c> DI container; an unkeyed <c>GetRequiredService</c>
/// would silently resolve whichever registration was added LAST across the
/// whole composition root (CP1 mandate §30).
/// </remarks>
public sealed class CommunicationMessageExecutionScope : ICommunicationMessageExecutionScope
{
    /// <summary>Keyed-DI key every Communication <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "communication";

    private readonly IServiceScopeFactory _scopeFactory;

    public CommunicationMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

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
