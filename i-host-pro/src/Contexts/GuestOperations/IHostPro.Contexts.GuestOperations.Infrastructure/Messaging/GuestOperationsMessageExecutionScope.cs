using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.GuestOperations.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Messaging;

/// <inheritdoc cref="IGuestOperationsMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Guest Operations authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-015/016 rationale. Opens a fresh Microsoft DI child
/// scope per message (rooted at the application's own root provider),
/// resolves <see cref="ITenantContext"/> from THAT scope and resolves it to
/// <c>tenantId</c> BEFORE resolving the business processor —
/// <c>GuestOperationsDbContext</c>/the transaction executor, resolved later
/// from the SAME scope via ordinary constructor injection, therefore
/// observe the SAME resolved <see cref="ITenantContext"/> instance, entirely
/// outside Wolverine's own per-message DI resolution.
///
/// Resolves <see cref="IIntegrationEventHandler{TMessage}"/> via
/// <see cref="HandlerKey"/> — <c>ReservationCreated</c> already has other
/// consumers (Housekeeping, Dashboard, Workflow, Communication) in the same
/// <c>IHostPro.Worker</c> process, so an unkeyed resolution would risk
/// silently resolving whichever registration was added LAST across the
/// whole composition root (the exact Fase 7 regression ADR-020 already
/// documents) — keyed from this context's very first commit.
/// </remarks>
public sealed class GuestOperationsMessageExecutionScope : IGuestOperationsMessageExecutionScope
{
    /// <summary>Keyed-DI key every Guest Operations <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "guestoperations";

    private readonly IServiceScopeFactory _scopeFactory;

    public GuestOperationsMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

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
