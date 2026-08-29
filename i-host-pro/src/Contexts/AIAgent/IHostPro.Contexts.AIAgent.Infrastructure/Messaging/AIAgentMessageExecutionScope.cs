using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.AIAgent.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Messaging;

/// <inheritdoc cref="IAIAgentMessageExecutionScope"/>
/// <remarks>
/// Deliberately the ONLY class in AI Agent authorized to hold an
/// <see cref="IServiceScopeFactory"/> — see the interface's own doc comment
/// for the full ADR-016 rationale. Opens a fresh Microsoft DI child scope
/// per message, resolves <see cref="ITenantContext"/> from THAT scope and
/// sets it to <c>tenantId</c> BEFORE resolving the business processor —
/// mirrors <c>CommunicationMessageExecutionScope</c> exactly.
/// </remarks>
public sealed class AIAgentMessageExecutionScope : IAIAgentMessageExecutionScope
{
    /// <summary>Keyed-DI key every AI Agent <c>IIntegrationEventHandler&lt;T&gt;</c> registration must use — see this class's own remarks.</summary>
    public const string HandlerKey = "aiagent";

    private readonly IServiceScopeFactory _scopeFactory;

    public AIAgentMessageExecutionScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

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
