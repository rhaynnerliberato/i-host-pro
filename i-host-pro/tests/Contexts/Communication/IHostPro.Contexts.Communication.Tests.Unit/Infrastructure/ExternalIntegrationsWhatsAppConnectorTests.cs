using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.Communication.Tests.Unit.Infrastructure;

/// <summary>
/// Fase 9, Checkpoint 2.2: <see cref="ExternalIntegrationsWhatsAppConnector"/> is a
/// thin adapter between Communication's own <see cref="OutboundMessageDispatch"/>/
/// <see cref="OutboundMessageDispatchResult"/> and
/// <c>ExternalIntegrations.Contracts.IMessagingProvider</c>'s
/// <see cref="OutboundMessageRequest"/>/<see cref="OutboundMessageResult"/> —
/// these tests prove the mapping in both directions, never touching any real
/// Meta HTTP call (that is <c>MetaWhatsAppMessagingProviderTests</c>'s own
/// scope, in <c>ExternalIntegrations.Tests.Unit</c>).
/// </summary>
public class ExternalIntegrationsWhatsAppConnectorTests
{
    private sealed class FakeMessagingProvider : IMessagingProvider
    {
        private readonly Func<OutboundMessageRequest, OutboundMessageResult> _behavior;

        private FakeMessagingProvider(Func<OutboundMessageRequest, OutboundMessageResult> behavior) => _behavior = behavior;

        public static FakeMessagingProvider Returning(OutboundMessageResult result) => new(_ => result);

        public OutboundMessageRequest? LastRequest { get; private set; }

        public Task<OutboundMessageResult> SendAsync(OutboundMessageRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_behavior(request));
        }
    }

    private static OutboundMessageDispatch BuildDispatch() => new(
        TenantId: Guid.NewGuid(), MessageId: Guid.NewGuid(), Destination: "+5511999998888",
        TemplateKey: "RESERVATION_CONFIRMATION",
        TemplateVariables: new Dictionary<string, string> { ["CheckInDate"] = "2026-08-20" },
        Content: "Check-in em 2026-08-20", IdempotencyKey: "idempotency-key");

    [Fact]
    public async Task SendAsync_maps_an_Accepted_result_to_a_successful_dispatch_with_the_providerMessageId()
    {
        var provider = FakeMessagingProvider.Returning(new OutboundMessageResult(true, "wamid.HBgL...", null, null));
        var connector = new ExternalIntegrationsWhatsAppConnector(provider);

        var result = await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ProviderMessageId.Should().Be("wamid.HBgL...");
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_maps_a_Rejected_result_to_a_failed_dispatch_carrying_the_providers_failure_code()
    {
        var provider = FakeMessagingProvider.Returning(
            new OutboundMessageResult(false, null, "131026", ProviderFailureCategory.InvalidRecipient));
        var connector = new ExternalIntegrationsWhatsAppConnector(provider);

        var result = await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ProviderMessageId.Should().BeNull();
        result.FailureReason.Should().Be("131026");
    }

    [Fact]
    public async Task SendAsync_maps_a_DeliveryOutcomeUnknown_result_to_a_distinct_failure_reason_never_a_retry_signal()
    {
        var provider = FakeMessagingProvider.Returning(
            new OutboundMessageResult(false, null, "timeout", ProviderFailureCategory.DeliveryOutcomeUnknown));
        var connector = new ExternalIntegrationsWhatsAppConnector(provider);

        var result = await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be("delivery_outcome_unknown",
            "the ambiguous/unknown outcome must be distinguishable from an explicit provider rejection");
    }

    [Fact]
    public async Task SendAsync_builds_the_provider_neutral_request_from_the_dispatch_without_ever_parsing_rendered_content()
    {
        var provider = FakeMessagingProvider.Returning(new OutboundMessageResult(true, "id", null, null));
        var connector = new ExternalIntegrationsWhatsAppConnector(provider);
        var dispatch = BuildDispatch();

        await connector.SendAsync(dispatch, CancellationToken.None);

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.TenantId.Should().Be(dispatch.TenantId);
        provider.LastRequest.MessageId.Should().Be(dispatch.MessageId);
        provider.LastRequest.Destination.Should().Be(dispatch.Destination);
        provider.LastRequest.Channel.Should().Be("WhatsApp");
        provider.LastRequest.TemplateKey.Should().Be(dispatch.TemplateKey);
        provider.LastRequest.TemplateVariables.Should().BeEquivalentTo(dispatch.TemplateVariables);
        provider.LastRequest.IdempotencyKey.Should().Be(dispatch.IdempotencyKey);
    }
}
