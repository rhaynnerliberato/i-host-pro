using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <inheritdoc cref="IHousekeepingMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Housekeeping authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-015 rationale. Opens a fresh Microsoft DI child scope
/// per message (rooted at the application's own root provider — a
/// <see cref="IServiceScopeFactory"/> resolved from ANY scope, including
/// Wolverine's own per-message scope, always creates children off the root
/// container, never off "the current scope"), resolves
/// <see cref="ITenantContext"/> from THAT scope and resolves it to
/// <c>tenantId</c> BEFORE resolving the business processor —
/// <c>HousekeepingDbContext</c>/the transaction executor, resolved later
/// from the SAME scope via ordinary constructor injection, therefore
/// observe the SAME resolved <see cref="ITenantContext"/> instance, entirely
/// outside Wolverine's own per-message DI resolution (which is where the
/// divergence was traced to — see ADR-015).
/// </remarks>
public sealed class HousekeepingMessageExecutionScope : IHousekeepingMessageExecutionScope
{
    private readonly IServiceScopeFactory _scopeFactory;

    public HousekeepingMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var processor = scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<TMessage>>();
        await processor.HandleAsync(message, cancellationToken);
    }
}
