using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <inheritdoc cref="IWhatsAppWebhookStatusEventPublisher"/>
/// <remarks>
/// A single Meta webhook HTTP delivery can legitimately batch status entries
/// for MULTIPLE tenants in one payload (ADR-022: App Secret/Verify Token are
/// app/deployment-level, one Meta App can have many tenants' phone numbers
/// registered under it — that is exactly why the routing directory exists).
/// <see cref="ITenantContext"/> is Scoped (one instance per HTTP request,
/// <c>IHostPro.Api</c>'s own <c>Program.cs</c>) and deliberately refuses to
/// be re-set to a DIFFERENT tenant within the same scope
/// (<c>TenantContext.SetTenant</c> throws) — a safety guard against
/// cross-tenant contamination, not an oversight. So this class cannot reuse
/// the ambient request-scoped <see cref="ITenantContext"/>/<see cref="IExternalIntegrationsTransactionExecutor"/>
/// directly; instead, mirroring <c>CommunicationMessageExecutionScope</c>'s
/// own ADR-016 pattern, it opens a FRESH child DI scope per outcome, resolves
/// a fresh <see cref="ITenantContext"/> from that scope, and sets it exactly
/// once — isolating each tenant's transaction from every other outcome in
/// the same webhook delivery. Deliberately the ONLY class in External
/// Integrations authorized to hold an <see cref="IServiceScopeFactory"/> —
/// see the architecture test enforcing this boundary.
///
/// Provider-neutral — maps <see cref="ProviderMessageStatus"/> (Domain) to
/// <see cref="WhatsAppMessageProviderStatus"/> (Contracts) explicitly; the
/// two are deliberately separate types (Contracts never references Domain,
/// ADR-021), so no implicit cast/shared enum exists to drift silently if one
/// changes without the other.
/// </remarks>
public sealed class WhatsAppWebhookStatusEventPublisher : IWhatsAppWebhookStatusEventPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;

    public WhatsAppWebhookStatusEventPublisher(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task PublishAsync(WebhookStatusProcessingOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Kind != WebhookStatusOutcomeKind.Accepted)
            throw new InvalidOperationException(
                $"Only {WebhookStatusOutcomeKind.Accepted} outcomes can be published — got {outcome.Kind}.");

        // Accepted is only ever produced once TenantId/ProviderMessageId/
        // NormalizedStatus/OccurredAtUtc have all been resolved — see
        // MetaWebhookStatusProcessor.BuildOutcome — so these are safe to
        // dereference here, never re-validated speculatively.
        var tenantId = outcome.TenantId!.Value;

        await using var scope = _scopeFactory.CreateAsyncScope();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        var transactionExecutor = scope.ServiceProvider.GetRequiredService<IExternalIntegrationsTransactionExecutor>();
        var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();

        await transactionExecutor.ExecuteAsync(() =>
        {
            collector.Enqueue(new WhatsAppMessageStatusChanged
            {
                TenantId = tenantId,
                AggregateId = Guid.NewGuid(),
                AggregateType = "WhatsAppMessageStatus",
                CorrelationId = Guid.NewGuid(),
                ActorType = "Integration",
                ProviderMessageId = outcome.ProviderMessageId!,
                Status = MapStatus(outcome.NormalizedStatus!.Value),
                OccurredAtUtc = outcome.OccurredAtUtc!.Value,
                ProviderErrorCode = outcome.ProviderErrorCode,
            });

            return Task.FromResult(true);
        }, cancellationToken);
    }

    private static WhatsAppMessageProviderStatus MapStatus(ProviderMessageStatus status) => status switch
    {
        ProviderMessageStatus.Sent => WhatsAppMessageProviderStatus.Sent,
        ProviderMessageStatus.Delivered => WhatsAppMessageProviderStatus.Delivered,
        ProviderMessageStatus.Read => WhatsAppMessageProviderStatus.Read,
        ProviderMessageStatus.Failed => WhatsAppMessageProviderStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unrecognized ProviderMessageStatus — MetaWebhookStatusProcessor should never produce an Accepted outcome for an unmapped status."),
    };
}
