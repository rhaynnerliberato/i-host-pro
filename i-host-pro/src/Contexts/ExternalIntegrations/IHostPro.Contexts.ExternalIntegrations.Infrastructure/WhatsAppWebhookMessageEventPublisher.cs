using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using DomainInboundGuestMessageType = IHostPro.Contexts.ExternalIntegrations.Domain.InboundGuestMessageType;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <inheritdoc cref="IWhatsAppWebhookMessageEventPublisher"/>
/// <remarks>
/// Mirrors <see cref="WhatsAppWebhookStatusEventPublisher"/> exactly,
/// including its fresh-child-DI-scope-per-outcome pattern (a single Meta
/// webhook HTTP delivery can legitimately batch entries for multiple
/// tenants). Deliberately one of only two classes in External Integrations
/// authorized to hold an <see cref="IServiceScopeFactory"/> — see the
/// architecture test enforcing this boundary.
/// </remarks>
public sealed class WhatsAppWebhookMessageEventPublisher : IWhatsAppWebhookMessageEventPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WhatsAppWebhookMessageEventPublisher(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task PublishAsync(WebhookMessageProcessingOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Kind != WebhookMessageOutcomeKind.Accepted)
            throw new InvalidOperationException(
                $"Only {WebhookMessageOutcomeKind.Accepted} outcomes can be published — got {outcome.Kind}.");

        // Accepted is only ever produced once every field below has been
        // resolved — see MetaWebhookMessageProcessor.BuildOutcome — so these
        // are safe to dereference here, never re-validated speculatively.
        var tenantId = outcome.TenantId!.Value;

        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var transactionExecutor = scope.ServiceProvider.GetRequiredService<IExternalIntegrationsTransactionExecutor>();
        var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();

        await transactionExecutor.ExecuteAsync(() =>
        {
            collector.Enqueue(new InboundGuestMessageReceived
            {
                TenantId = tenantId,
                AggregateId = Guid.NewGuid(),
                AggregateType = "InboundGuestMessage",
                CorrelationId = Guid.NewGuid(),
                ActorType = "Integration",
                ProviderMessageId = outcome.ProviderMessageId!,
                Channel = "WhatsApp",
                SenderPhoneNormalized = outcome.SenderPhoneNormalized!,
                MessageType = MapMessageType(outcome.MessageType!.Value),
                Text = outcome.Text,
                OccurredAtUtc = outcome.OccurredAtUtc!.Value,
            });

            return Task.FromResult(true);
        }, cancellationToken);
    }

    private static InboundGuestMessageType MapMessageType(DomainInboundGuestMessageType messageType) => messageType switch
    {
        DomainInboundGuestMessageType.Text => InboundGuestMessageType.Text,
        DomainInboundGuestMessageType.Unsupported => InboundGuestMessageType.Unsupported,
        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unrecognized InboundGuestMessageType — MetaWebhookMessageProcessor should never produce an Accepted outcome for an unmapped type."),
    };
}
