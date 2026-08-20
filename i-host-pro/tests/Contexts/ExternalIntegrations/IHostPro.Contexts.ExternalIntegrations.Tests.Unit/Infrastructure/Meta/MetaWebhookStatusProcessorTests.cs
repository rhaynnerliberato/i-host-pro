using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

/// <summary>
/// Fase 9, Checkpoint 2.3.2: deterministic coverage of Meta envelope
/// parsing/route resolution/status normalization — never a real database,
/// never a real Meta call. The known/unknown route distinction is driven by
/// a fake <see cref="IWhatsAppTenantRouteResolver"/>.
/// </summary>
public class MetaWebhookStatusProcessorTests
{
    private static readonly Guid KnownTenantId = Guid.NewGuid();
    private const string KnownPhoneNumberId = "known-phone-id";
    private const string UnknownPhoneNumberId = "unknown-phone-id";

    private static MetaWebhookStatusProcessor BuildProcessor() =>
        new(new FakeTenantRouteResolver());

    [Fact]
    public async Task A_known_route_with_a_recognized_status_is_Accepted_and_normalized()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", "delivered", "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Accepted);
        outcomes[0].TenantId.Should().Be(KnownTenantId);
        outcomes[0].NormalizedStatus.Should().Be(ProviderMessageStatus.Delivered);
        outcomes[0].ProviderMessageId.Should().Be("wamid.ABC");
        outcomes[0].OccurredAtUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1750030073));
    }

    [Fact]
    public async Task An_unknown_phone_number_id_is_UnknownRoute_with_no_tenant()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(UnknownPhoneNumberId, "wamid.ABC", "sent", "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.UnknownRoute);
        outcomes[0].TenantId.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_ProviderMessageId_is_Malformed()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, id: null, status: "sent", timestamp: "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task A_missing_status_is_Malformed()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", status: null, timestamp: "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task A_missing_timestamp_is_Malformed()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", "sent", timestamp: null);

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task A_non_numeric_timestamp_is_Malformed_never_substituted_with_UtcNow()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", "sent", timestamp: "not-a-number");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
        outcomes[0].OccurredAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task An_unrecognized_status_string_is_Malformed()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", "some_future_status", "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task Played_status_is_deferred_as_Malformed_never_modeled_as_a_real_status()
    {
        var processor = BuildProcessor();
        var body = BuildEnvelope(KnownPhoneNumberId, "wamid.ABC", "played", "1750030073");

        var outcomes = await processor.ProcessAsync(body, CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task A_failed_status_extracts_only_the_error_code()
    {
        var processor = BuildProcessor();
        var body = """
            {"entry":[{"changes":[{"value":{
                "metadata":{"phone_number_id":"__PHONE__"},
                "statuses":[{"id":"wamid.ABC","status":"failed","timestamp":"1750030073",
                    "errors":[{"code":131049,"title":"ignored","message":"ignored"}]}]
            }}]}]}
            """.Replace("__PHONE__", KnownPhoneNumberId);

        var outcomes = await processor.ProcessAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);

        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Accepted);
        outcomes[0].NormalizedStatus.Should().Be(ProviderMessageStatus.Failed);
        outcomes[0].ProviderErrorCode.Should().Be(131049);
    }

    [Fact]
    public async Task A_non_failed_status_never_carries_an_error_code_even_if_present()
    {
        var processor = BuildProcessor();
        var body = """
            {"entry":[{"changes":[{"value":{
                "metadata":{"phone_number_id":"__PHONE__"},
                "statuses":[{"id":"wamid.ABC","status":"sent","timestamp":"1750030073",
                    "errors":[{"code":999}]}]
            }}]}]}
            """.Replace("__PHONE__", KnownPhoneNumberId);

        var outcomes = await processor.ProcessAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);

        outcomes[0].ProviderErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task An_inbound_message_webhook_with_no_statuses_is_ignored_not_malformed()
    {
        var processor = BuildProcessor();
        var body = """
            {"entry":[{"changes":[{"value":{
                "metadata":{"phone_number_id":"__PHONE__"},
                "messages":[{"from":"16505551234","id":"wamid.XYZ","type":"text"}]
            }}]}]}
            """.Replace("__PHONE__", KnownPhoneNumberId);

        var outcomes = await processor.ProcessAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);

        outcomes.Should().BeEmpty("an inbound-message webhook is deferred, never treated as a business defect");
    }

    [Fact]
    public async Task Malformed_JSON_produces_a_single_Malformed_outcome()
    {
        var processor = BuildProcessor();

        var outcomes = await processor.ProcessAsync("not json at all"u8.ToArray(), CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task An_envelope_with_no_entries_produces_a_single_Malformed_outcome()
    {
        var processor = BuildProcessor();

        var outcomes = await processor.ProcessAsync("{\"entry\":[]}"u8.ToArray(), CancellationToken.None);

        outcomes.Should().ContainSingle();
        outcomes[0].Kind.Should().Be(WebhookStatusOutcomeKind.Malformed);
    }

    [Fact]
    public async Task Multiple_status_entries_in_one_payload_are_all_processed()
    {
        var processor = BuildProcessor();
        var body = """
            {"entry":[{"changes":[{"value":{
                "metadata":{"phone_number_id":"__PHONE__"},
                "statuses":[
                    {"id":"wamid.ONE","status":"sent","timestamp":"1750030073"},
                    {"id":"wamid.TWO","status":"delivered","timestamp":"1750030080"}
                ]
            }}]}]}
            """.Replace("__PHONE__", KnownPhoneNumberId);

        var outcomes = await processor.ProcessAsync(Encoding.UTF8.GetBytes(body), CancellationToken.None);

        outcomes.Should().HaveCount(2);
        outcomes[0].ProviderMessageId.Should().Be("wamid.ONE");
        outcomes[1].ProviderMessageId.Should().Be("wamid.TWO");
    }

    private static byte[] BuildEnvelope(string phoneNumberId, string? id, string? status, string? timestamp)
    {
        var fields = new List<string> { "\"_end\":null" };
        if (id is not null) fields.Add($"\"id\":\"{id}\"");
        if (status is not null) fields.Add($"\"status\":\"{status}\"");
        if (timestamp is not null) fields.Add($"\"timestamp\":\"{timestamp}\"");
        var statusJson = "{" + string.Join(",", fields) + "}";

        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + phoneNumberId + "\"}," +
            "\"statuses\":[" + statusJson + "]" +
            "}}]}]}";

        return Encoding.UTF8.GetBytes(body);
    }

    private sealed class FakeTenantRouteResolver : IWhatsAppTenantRouteResolver
    {
        public Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken) =>
            Task.FromResult(phoneNumberId == KnownPhoneNumberId ? (Guid?)KnownTenantId : null);
    }
}
