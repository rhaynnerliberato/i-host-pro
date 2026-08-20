using System.Net;
using System.Text.Json;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppTemplateMappings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

/// <summary>
/// Fase 9, Checkpoint 2.2 mandate §39-41: deterministic HTTP contract tests
/// for <see cref="MetaWhatsAppMessagingProvider"/> — no live internet
/// dependency (<see cref="RecordingHttpMessageHandler"/>), proving the exact
/// outbound request shape and the response/error mapping to
/// <see cref="OutboundMessageResult"/>. Never exercises a real Meta endpoint.
/// </summary>
public class MetaWhatsAppMessagingProviderTests
{
    private const string SentinelAccessToken = "SENTINEL_ACCESS_TOKEN_never_logged";
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();

    private static WhatsAppIntegration BuildIntegration()
    {
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), TenantId, DateTimeOffset.UtcNow);
        integration.UpdateConfiguration("waba-1", "1234567890", "access-token-ref", null, null, DateTimeOffset.UtcNow);
        return integration;
    }

    private static WhatsAppTemplateMapping BuildMapping() => WhatsAppTemplateMapping.Create(
        Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR", ["CheckInDate"], DateTimeOffset.UtcNow);

    private static OutboundMessageRequest BuildRequest() => new(
        TenantId, MessageId, "WhatsApp", "+5511999998888",
        "RESERVATION_CONFIRMATION", new Dictionary<string, string> { ["CheckInDate"] = "2026-08-20" }, "idempotency-key");

    private static MetaWhatsAppMessagingProvider BuildProvider(
        RecordingHttpMessageHandler handler,
        WhatsAppIntegration? integration,
        WhatsAppTemplateMapping? mapping,
        string? accessTokenValue = SentinelAccessToken) =>
        new(
            new FakeHttpClientFactory(handler),
            FakeWhatsAppIntegrationRepository.WithExisting(integration),
            FakeWhatsAppTemplateMappingRepository.WithExisting(mapping),
            FakeWhatsAppCredentialProvider.Returning(accessTokenValue),
            Options.Create(new MetaWhatsAppOptions { GraphApiVersion = "v26.0" }),
            NullLogger<MetaWhatsAppMessagingProvider>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body)),
    };

    // ---- Request shape ------------------------------------------------------

    [Fact]
    public async Task SendAsync_builds_the_exact_documented_request_shape()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { messaging_product = "whatsapp", messages = new[] { new { id = "wamid.ABC" } } }));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        await provider.SendAsync(BuildRequest(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://graph.facebook.com/v26.0/1234567890/messages");
        request.AuthorizationHeader.Should().Be($"Bearer {SentinelAccessToken}");

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        root.GetProperty("messaging_product").GetString().Should().Be("whatsapp");
        root.GetProperty("type").GetString().Should().Be("template");
        root.GetProperty("to").GetString().Should().Be("+5511999998888");

        var template = root.GetProperty("template");
        template.GetProperty("name").GetString().Should().Be("reservation_confirmation_v1");
        template.GetProperty("language").GetProperty("code").GetString().Should().Be("pt_BR");

        var components = template.GetProperty("components");
        components.GetArrayLength().Should().Be(1);
        var bodyComponent = components[0];
        bodyComponent.GetProperty("type").GetString().Should().Be("body");
        var parameters = bodyComponent.GetProperty("parameters");
        parameters.GetArrayLength().Should().Be(1);
        parameters[0].GetProperty("type").GetString().Should().Be("text");
        parameters[0].GetProperty("text").GetString().Should().Be("2026-08-20");
    }

    [Fact]
    public async Task SendAsync_orders_parameters_by_the_mappings_own_parameterOrder_never_dictionary_order()
    {
        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "name", "pt_BR", ["GuestName", "CheckInDate"], DateTimeOffset.UtcNow);
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { messages = new[] { new { id = "wamid.ABC" } } }));
        var provider = BuildProvider(handler, BuildIntegration(), mapping);
        var request = new OutboundMessageRequest(
            TenantId, MessageId, "WhatsApp", "+5511999998888", "RESERVATION_CONFIRMATION",
            new Dictionary<string, string> { ["CheckInDate"] = "2026-08-20", ["GuestName"] = "Maria" }, "key");

        await provider.SendAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var parameters = body.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        parameters[0].GetProperty("text").GetString().Should().Be("Maria");
        parameters[1].GetProperty("text").GetString().Should().Be("2026-08-20");
    }

    [Fact]
    public async Task SendAsync_omits_the_components_array_entirely_for_a_zero_parameter_template()
    {
        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "hello_world", "en_US", [], DateTimeOffset.UtcNow);
        var request = new OutboundMessageRequest(
            TenantId, MessageId, "WhatsApp", "+5511999998888", "RESERVATION_CONFIRMATION",
            new Dictionary<string, string>(), "idempotency-key");
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { messages = new[] { new { id = "wamid.ABC" } } }));
        var provider = BuildProvider(handler, BuildIntegration(), mapping);

        await provider.SendAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = body.RootElement;
        root.GetProperty("type").GetString().Should().Be("template");

        var template = root.GetProperty("template");
        template.GetProperty("name").GetString().Should().Be("hello_world");
        template.GetProperty("language").GetProperty("code").GetString().Should().Be("en_US");
        template.TryGetProperty("components", out _).Should().BeFalse(
            "a zero-parameter template must omit the components key entirely, not send it as null");
    }

    // ---- Success mapping ------------------------------------------------------

    [Fact]
    public async Task SendAsync_maps_an_accepted_response_to_Accepted_with_the_real_providerMessageId()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { messages = new[] { new { id = "wamid.HBgLABC123" } } }));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeTrue();
        result.ProviderMessageId.Should().Be("wamid.HBgLABC123");
        result.FailureCategory.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_maps_a_malformed_success_response_to_a_safe_DeliveryOutcomeUnknown_failure()
    {
        var handler = RecordingHttpMessageHandler.Returning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all"),
        });
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ProviderMessageId.Should().BeNull();
        result.FailureCategory.Should().Be(ProviderFailureCategory.DeliveryOutcomeUnknown,
            "a 200 OK we cannot parse is not proof the message was NOT accepted — never treated as a clean rejection");
    }

    // ---- Error mapping ------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, ProviderFailureCategory.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, null, ProviderFailureCategory.AuthenticationFailed)]
    [InlineData(HttpStatusCode.BadRequest, 190, ProviderFailureCategory.AuthenticationFailed)]
    [InlineData(HttpStatusCode.BadRequest, 131026, ProviderFailureCategory.InvalidRecipient)]
    [InlineData(HttpStatusCode.BadRequest, 132001, ProviderFailureCategory.InvalidTemplate)]
    [InlineData(HttpStatusCode.TooManyRequests, null, ProviderFailureCategory.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, 80007, ProviderFailureCategory.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, null, ProviderFailureCategory.TransientProviderFailure)]
    [InlineData(HttpStatusCode.BadRequest, 999999, ProviderFailureCategory.PermanentFailure)]
    public async Task SendAsync_maps_documented_Meta_errors_to_the_correct_provider_neutral_category(
        HttpStatusCode status, int? errorCode, ProviderFailureCategory expectedCategory)
    {
        var errorBody = errorCode is null
            ? new { error = new { message = "failure", type = "OAuthException" } }
            : (object)new { error = new { message = "failure", type = "OAuthException", code = errorCode } };
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(status, errorBody));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.ProviderMessageId.Should().BeNull();
        result.FailureCategory.Should().Be(expectedCategory);
        handler.Requests.Should().ContainSingle("no automatic retry may ever occur");
    }

    // ---- Timeout / network interruption -> DeliveryOutcomeUnknown, never retried ----

    [Fact]
    public async Task SendAsync_maps_a_client_side_timeout_to_DeliveryOutcomeUnknown_without_retrying()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new TaskCanceledException("The request timed out."));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureCategory.Should().Be(ProviderFailureCategory.DeliveryOutcomeUnknown,
            "a timeout after the request may have already reached Meta — never a confirmed rejection, never auto-retried");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SendAsync_maps_a_network_interruption_to_DeliveryOutcomeUnknown_without_retrying()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new HttpRequestException("Connection reset"));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureCategory.Should().Be(ProviderFailureCategory.DeliveryOutcomeUnknown);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SendAsync_propagates_a_caller_initiated_cancellation_instead_of_reclassifying_it_as_DeliveryOutcomeUnknown()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new TaskCanceledException());
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await provider.SendAsync(BuildRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a caller-initiated cancellation is not an ambiguous provider outcome — it must propagate, never be swallowed into DeliveryOutcomeUnknown");
    }

    // ---- Configuration/credential gaps -> safe rejection, never a crash ----

    [Fact]
    public async Task SendAsync_rejects_without_any_HTTP_call_when_no_integration_is_configured()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(HttpStatusCode.OK, new { }));
        var provider = BuildProvider(handler, integration: null, BuildMapping());

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_rejects_with_AuthenticationFailed_without_any_HTTP_call_when_the_credential_provider_returns_nothing()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(HttpStatusCode.OK, new { }));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping(), accessTokenValue: null);

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureCategory.Should().Be(ProviderFailureCategory.AuthenticationFailed);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_rejects_with_InvalidTemplate_without_any_HTTP_call_when_no_mapping_exists_for_the_templateKey()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(HttpStatusCode.OK, new { }));
        var provider = BuildProvider(handler, BuildIntegration(), mapping: null);

        var result = await provider.SendAsync(BuildRequest(), CancellationToken.None);

        result.Accepted.Should().BeFalse();
        result.FailureCategory.Should().Be(ProviderFailureCategory.InvalidTemplate);
        handler.Requests.Should().BeEmpty();
    }

    // ---- Secret safety --------------------------------------------------------

    [Fact]
    public async Task The_access_token_appears_only_in_the_wire_Authorization_header_never_in_the_request_body_or_URL()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, new { messages = new[] { new { id = "wamid.ABC" } } }));
        var provider = BuildProvider(handler, BuildIntegration(), BuildMapping());

        await provider.SendAsync(BuildRequest(), CancellationToken.None);

        var request = handler.Requests[0];
        request.AuthorizationHeader.Should().Contain(SentinelAccessToken);
        request.Uri.ToString().Should().NotContain(SentinelAccessToken);
        request.Body.Should().NotContain(SentinelAccessToken);
    }
}
