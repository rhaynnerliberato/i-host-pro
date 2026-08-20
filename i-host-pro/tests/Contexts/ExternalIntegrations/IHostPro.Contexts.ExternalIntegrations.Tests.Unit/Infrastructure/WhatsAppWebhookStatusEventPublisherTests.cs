using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 (ADR-022 item 13/14). Covers the regression this
/// class's own child-DI-scope-per-outcome design fixes: a single Meta
/// webhook delivery batching status entries for MULTIPLE tenants must never
/// fail — <see cref="TenantContext.SetTenant"/> deliberately throws if
/// re-set to a DIFFERENT tenant within the same scope, so reusing one
/// request-scoped <see cref="ITenantContext"/> across outcomes would break
/// on the very first multi-tenant batch.
/// </summary>
public class WhatsAppWebhookStatusEventPublisherTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static (WhatsAppWebhookStatusEventPublisher Publisher, RecordingIntegrationEventCollector Collector) CreatePublisher()
    {
        var collector = new RecordingIntegrationEventCollector();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IExternalIntegrationsTransactionExecutor, PassThroughExternalIntegrationsTransactionExecutor>();
        services.AddSingleton<IIntegrationEventCollector>(collector);

        var provider = services.BuildServiceProvider();
        var publisher = new WhatsAppWebhookStatusEventPublisher(provider.GetRequiredService<IServiceScopeFactory>());

        return (publisher, collector);
    }

    private static WebhookStatusProcessingOutcome AcceptedOutcome(
        Guid tenantId, ProviderMessageStatus status, string providerMessageId = "wamid.HBgL...", int? errorCode = null) =>
        new(WebhookStatusOutcomeKind.Accepted, tenantId, providerMessageId, status, OccurredAt, errorCode);

    [Theory]
    [InlineData(WebhookStatusOutcomeKind.UnknownRoute)]
    [InlineData(WebhookStatusOutcomeKind.Malformed)]
    public async Task PublishAsync_rejects_a_non_Accepted_outcome(WebhookStatusOutcomeKind kind)
    {
        var (publisher, _) = CreatePublisher();
        var outcome = new WebhookStatusProcessingOutcome(kind, null, null, null, null, null);

        var act = () => publisher.PublishAsync(outcome, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PublishAsync_enqueues_a_correctly_mapped_event()
    {
        var (publisher, collector) = CreatePublisher();
        var outcome = AcceptedOutcome(TenantA, ProviderMessageStatus.Delivered, "wamid.ABC", errorCode: null);

        await publisher.PublishAsync(outcome, CancellationToken.None);

        var published = collector.Enqueued.Should().ContainSingle().Which.Should().BeOfType<WhatsAppMessageStatusChanged>().Subject;
        published.TenantId.Should().Be(TenantA);
        published.ProviderMessageId.Should().Be("wamid.ABC");
        published.Status.Should().Be(WhatsAppMessageProviderStatus.Delivered);
        published.OccurredAtUtc.Should().Be(OccurredAt);
        published.ProviderErrorCode.Should().BeNull();
        published.ActorType.Should().Be("Integration");
    }

    [Theory]
    [InlineData(ProviderMessageStatus.Sent, WhatsAppMessageProviderStatus.Sent)]
    [InlineData(ProviderMessageStatus.Delivered, WhatsAppMessageProviderStatus.Delivered)]
    [InlineData(ProviderMessageStatus.Read, WhatsAppMessageProviderStatus.Read)]
    [InlineData(ProviderMessageStatus.Failed, WhatsAppMessageProviderStatus.Failed)]
    public async Task PublishAsync_maps_every_ProviderMessageStatus_to_its_Contracts_equivalent(
        ProviderMessageStatus domainStatus, WhatsAppMessageProviderStatus contractsStatus)
    {
        var (publisher, collector) = CreatePublisher();

        await publisher.PublishAsync(AcceptedOutcome(TenantA, domainStatus), CancellationToken.None);

        ((WhatsAppMessageStatusChanged)collector.Enqueued.Single()).Status.Should().Be(contractsStatus);
    }

    [Fact]
    public async Task PublishAsync_includes_the_provider_error_code_only_when_present()
    {
        var (publisher, collector) = CreatePublisher();

        await publisher.PublishAsync(AcceptedOutcome(TenantA, ProviderMessageStatus.Failed, errorCode: 131026), CancellationToken.None);

        ((WhatsAppMessageStatusChanged)collector.Enqueued.Single()).ProviderErrorCode.Should().Be(131026);
    }

    [Fact]
    public async Task PublishAsync_handles_a_single_delivery_batching_two_DIFFERENT_tenants()
    {
        // The regression this class's fresh-scope-per-outcome design fixes:
        // one Meta webhook HTTP delivery can legitimately carry status
        // entries for multiple tenants (ADR-022: one Meta App, many tenant
        // phone numbers). A shared, request-scoped ITenantContext would
        // throw on the second SetTenant call with a different tenant.
        var (publisher, collector) = CreatePublisher();

        await publisher.PublishAsync(AcceptedOutcome(TenantA, ProviderMessageStatus.Sent, "wamid.A"), CancellationToken.None);
        await publisher.PublishAsync(AcceptedOutcome(TenantB, ProviderMessageStatus.Sent, "wamid.B"), CancellationToken.None);

        collector.Enqueued.Should().HaveCount(2);
        collector.Enqueued.Cast<WhatsAppMessageStatusChanged>().Select(e => e.TenantId).Should().BeEquivalentTo([TenantA, TenantB]);
    }

    [Fact]
    public async Task PublishAsync_generates_a_fresh_CorrelationId_and_AggregateId_per_event()
    {
        var (publisher, collector) = CreatePublisher();

        await publisher.PublishAsync(AcceptedOutcome(TenantA, ProviderMessageStatus.Sent, "wamid.A"), CancellationToken.None);
        await publisher.PublishAsync(AcceptedOutcome(TenantA, ProviderMessageStatus.Delivered, "wamid.A"), CancellationToken.None);

        var events = collector.Enqueued.Cast<WhatsAppMessageStatusChanged>().ToList();
        events[0].CorrelationId.Should().NotBe(events[1].CorrelationId);
        events[0].AggregateId.Should().NotBe(events[1].AggregateId);
    }
}
