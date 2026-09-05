using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Infrastructure;

/// <summary>
/// CP5.3E corrective fix: <see cref="NotConfiguredOutboundMessageConnector"/>
/// is the <see cref="IOutboundMessageConnector"/> registered for every
/// non-Development environment. These tests prove its one contract: it
/// always resolves (no exception, no network call) and always reports an
/// explicit, deterministic failure — never success, never a real
/// ProviderMessageId, never anything that could let a caller mistake it for
/// a real delivery.
/// </summary>
public class NotConfiguredOutboundMessageConnectorTests
{
    private static OutboundMessageDispatch BuildDispatch() => new(
        TenantId: Guid.NewGuid(), MessageId: Guid.NewGuid(), Destination: "+5511999998888",
        TemplateKey: "AI_AGENT_RESPONSE", TemplateVariables: new Dictionary<string, string>(),
        Content: "Sua reserva está confirmada.", IdempotencyKey: "idempotency-key");

    [Fact]
    public async Task SendAsync_never_reports_success()
    {
        var connector = new NotConfiguredOutboundMessageConnector(NullLogger<NotConfiguredOutboundMessageConnector>.Instance);

        var result = await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_reports_the_explicit_not_configured_failure_reason_and_no_ProviderMessageId()
    {
        var connector = new NotConfiguredOutboundMessageConnector(NullLogger<NotConfiguredOutboundMessageConnector>.Instance);

        var result = await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        result.FailureReason.Should().Be("outbound_channel_not_configured");
        result.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_never_throws()
    {
        var connector = new NotConfiguredOutboundMessageConnector(NullLogger<NotConfiguredOutboundMessageConnector>.Instance);

        var act = async () => await connector.SendAsync(BuildDispatch(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
