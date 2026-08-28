using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Payments.Infrastructure.Messaging;

/// <inheritdoc cref="IPaymentsMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in Payments authorized to hold an
/// <see cref="IServiceScopeFactory"/> — mirrors every other Bounded
/// Context's own execution scope (ADR-015/016). Opens a fresh Microsoft DI
/// child scope per message, resolves <see cref="ITenantContext"/> from THAT
/// scope and resolves it to the caller-supplied tenant id BEFORE resolving
/// the business handler.
///
/// Resolves <see cref="IIntegrationEventHandler{TMessage}"/> via
/// <see cref="HandlerKey"/> — same keyed-DI convention as every other
/// Bounded Context's execution scope, defending against Wolverine's own
/// handler-chain-combining behavior (ADR-020) if a second in-process
/// consumer of the same message type is ever added.
/// </remarks>
public sealed class PaymentsMessageExecutionScope : IPaymentsMessageExecutionScope
{
    /// <summary>Keyed-DI key every Payments <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "payments";

    private readonly IServiceScopeFactory _scopeFactory;

    public PaymentsMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task ExecuteAsync<TMessage>(
        TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        where TMessage : IntegrationEvent
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var handler = scope.ServiceProvider.GetRequiredKeyedService<IIntegrationEventHandler<TMessage>>(HandlerKey);
        await handler.HandleAsync(message, cancellationToken);
    }

    public async Task ExecutePixChargeConfirmationReceivedAsync(
        PixChargeConfirmationReceived message, Guid messageId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(message.TenantId);

        var handler = scope.ServiceProvider.GetRequiredService<IPixChargeConfirmationReceivedHandler>();
        await handler.HandleAsync(message, cancellationToken);
    }
}
