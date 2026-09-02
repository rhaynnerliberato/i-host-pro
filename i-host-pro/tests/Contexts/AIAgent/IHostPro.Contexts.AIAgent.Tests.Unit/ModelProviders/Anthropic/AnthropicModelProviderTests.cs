using System.Net;
using System.Text.Json;
using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.ModelProviders.Anthropic;

/// <summary>
/// Fase 11, Checkpoint 7, mandate item 61/62 — deterministic HTTP contract
/// tests for <see cref="AnthropicModelProvider"/> (<see cref="RecordingHttpMessageHandler"/>,
/// no live internet dependency). Proves the exact outbound request shape
/// (including the explicit absence of <c>temperature</c> — mandate item 13),
/// the response/error mapping to <see cref="ModelResult"/>, cost
/// calculation, and secret safety. Never exercises the real Anthropic API.
/// </summary>
public class AnthropicModelProviderTests
{
    private const string SentinelApiKey = "sk-ant-SENTINEL_never_logged";

    private static readonly ModelRequest SimpleRequest = new(
        SystemPrompt: "system instructions",
        Messages: [new ModelMessage(ModelMessageRole.Guest, "Olá, preciso de ajuda")]);

    private static AnthropicModelProvider BuildProvider(
        RecordingHttpMessageHandler handler, string? apiKey = SentinelApiKey, AnthropicOptions? options = null) =>
        new(
            new FakeHttpClientFactory(handler),
            FakeAnthropicCredentialProvider.Returning(apiKey),
            Options.Create(options ?? new AnthropicOptions()),
            NullLogger<AnthropicModelProvider>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body)),
    };

    private static object RespondToGuestToolUseResponse(
        string message, string language, string? intent = null, bool? confirmationIntent = null, string model = "claude-sonnet-4-6")
    {
        var input = new Dictionary<string, object?> { ["message"] = message, ["language"] = language };
        if (intent is not null)
            input["intent"] = intent;
        if (confirmationIntent is not null)
            input["confirmation_intent"] = confirmationIntent;

        return new
        {
            id = "msg_01",
            model,
            content = new object[] { new { type = "tool_use", id = "toolu_01", name = "respond_to_guest", input } },
            stop_reason = "tool_use",
            usage = new { input_tokens = 120, output_tokens = 40 },
        };
    }

    private static object BusinessToolUseResponse(string toolName, object input, string model = "claude-sonnet-4-6") => new
    {
        id = "msg_01",
        model,
        content = new object[] { new { type = "tool_use", id = "toolu_02", name = toolName, input } },
        stop_reason = "tool_use",
        usage = new { input_tokens = 200, output_tokens = 15 },
    };

    // ---- Request shape ------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_builds_the_exact_documented_request_shape()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Olá! Como posso ajudar?", "pt-BR")));
        var provider = BuildProvider(handler);

        await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://api.anthropic.com/v1/messages");
        request.Headers["x-api-key"].Should().Be(SentinelApiKey);
        request.Headers["anthropic-version"].Should().Be("2023-06-01");

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-sonnet-4-6");
        root.GetProperty("max_tokens").GetInt32().Should().Be(2048);
        root.GetProperty("system").GetString().Should().Be("system instructions");

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
    }

    [Fact]
    public async Task GenerateAsync_never_sends_a_temperature_field_since_claude_sonnet_4_6_rejects_any_custom_value()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Olá!", "pt-BR")));
        var provider = BuildProvider(handler);

        await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        body.RootElement.TryGetProperty("temperature", out _).Should().BeFalse(
            "claude-sonnet-4-6 rejects any temperature value other than 1.0 with HTTP 400 — the field must never be sent at all (CP7 governance record)");
    }

    [Fact]
    public async Task GenerateAsync_offers_business_tools_plus_respond_to_guest_with_tool_choice_any_on_the_first_call()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Olá!", "pt-BR")));
        var provider = BuildProvider(handler);
        var request = SimpleRequest with
        {
            AvailableTools = [new AgentToolDescriptor("GetReservationSummary", "Resumo da reserva.")],
        };

        await provider.GenerateAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = body.RootElement;
        var toolNames = root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        toolNames.Should().BeEquivalentTo(["GetReservationSummary", "respond_to_guest"]);
        root.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("any");
    }

    [Fact]
    public async Task GenerateAsync_forces_respond_to_guest_alone_on_the_second_call_after_a_tool_result()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Sua reserva está confirmada.", "pt-BR")));
        var provider = BuildProvider(handler);
        var request = SimpleRequest with
        {
            AvailableTools = [new AgentToolDescriptor("GetReservationSummary", "Resumo da reserva.")],
            Messages = [.. SimpleRequest.Messages, new ModelMessage(ModelMessageRole.Tool, "Status: Confirmed.")],
        };

        await provider.GenerateAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = body.RootElement;
        var toolNames = root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        toolNames.Should().BeEquivalentTo(["respond_to_guest"], "Checkpoint 3's own no-multi-hop rule — the model never gets a second chance to request a different Tool");
        var toolChoice = root.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().Should().Be("tool");
        toolChoice.GetProperty("name").GetString().Should().Be("respond_to_guest");
    }

    [Fact]
    public async Task GenerateAsync_sends_a_Tool_role_turn_as_a_prefixed_user_message_never_a_native_tool_result_block()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Confirmado.", "pt-BR")));
        var provider = BuildProvider(handler);
        var request = SimpleRequest with
        {
            Messages = [.. SimpleRequest.Messages, new ModelMessage(ModelMessageRole.Tool, "Status: Confirmed.")],
        };

        await provider.GenerateAsync(request, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var messages = body.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);
        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("Status: Confirmed.");
    }

    // ---- Response mapping: respond_to_guest ----------------------------------

    [Fact]
    public async Task GenerateAsync_maps_a_respond_to_guest_tool_use_to_Text_Language_and_real_usage()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Sua reserva está confirmada.", "pt-BR")));
        var provider = BuildProvider(handler);

        var result = await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        result.Text.Should().Be("Sua reserva está confirmada.");
        result.DetectedLanguage.Should().Be("pt-BR");
        result.ToolCallRequest.Should().BeNull();
        result.InputTokens.Should().Be(120);
        result.OutputTokens.Should().Be(40);
        result.ModelName.Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    public async Task GenerateAsync_maps_the_intent_field_when_present()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Vou encaminhar seu pedido.", "pt-BR", intent: "refund")));
        var provider = BuildProvider(handler);

        var result = await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        result.Intent.Should().Be("refund");
    }

    [Fact]
    public async Task GenerateAsync_leaves_Intent_null_when_the_model_omits_it()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Claro, posso ajudar.", "pt-BR")));
        var provider = BuildProvider(handler);

        var result = await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        result.Intent.Should().BeNull();
        result.Confidence.Should().BeNull("CP7 never fabricates a numeric confidence — NumericConfidenceThreshold=false");
    }

    [Fact]
    public async Task GenerateAsync_maps_confirmation_intent_when_present()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Confirmado!", "pt-BR", confirmationIntent: true)));
        var provider = BuildProvider(handler);

        var result = await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        result.ConfirmationIntent.Should().BeTrue();
    }

    // ---- Response mapping: business tool call --------------------------------

    [Fact]
    public async Task GenerateAsync_maps_a_business_tool_use_to_ToolCallRequest_with_string_arguments()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(
            HttpStatusCode.OK,
            BusinessToolUseResponse("RequestEarlyCheckIn", new { requestedCheckInAt = "2026-09-02T12:00:00-03:00" })));
        var provider = BuildProvider(handler);
        var request = SimpleRequest with { AvailableTools = [new AgentToolDescriptor("RequestEarlyCheckIn", "Early check-in.")] };

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        result.ToolCallRequest.Should().NotBeNull();
        result.ToolCallRequest!.ToolName.Should().Be("RequestEarlyCheckIn");
        result.ToolCallRequest.Arguments.Should().ContainKey("requestedCheckInAt")
            .WhoseValue.Should().Be("2026-09-02T12:00:00-03:00");
        result.Text.Should().BeEmpty();
    }

    // ---- Cost calculation -----------------------------------------------------

    [Fact]
    public async Task GenerateAsync_computes_EstimatedCostUsd_from_real_usage_and_configured_pricing()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Olá!", "pt-BR")));
        var options = new AnthropicOptions
        {
            Pricing = new AnthropicPricingOptions { InputUsdPerMillionTokens = 3m, OutputUsdPerMillionTokens = 15m, Reference = "claude-sonnet-4-6" },
        };
        var provider = BuildProvider(handler, options: options);

        var result = await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        // usage: input=120, output=40 (RespondToGuestToolUseResponse's own fixed usage)
        var expectedCost = (120 / 1_000_000m * 3m) + (40 / 1_000_000m * 15m);
        result.EstimatedCostUsd.Should().Be(expectedCost);
        result.CostPricingReference.Should().Be("claude-sonnet-4-6");
    }

    // ---- Error mapping ----------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task GenerateAsync_classifies_HTTP_failures_as_permanent_or_transient_correctly(
        HttpStatusCode status, bool expectedPermanent)
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(status, new { error = new { type = "some_error", message = "failure" } }));
        var provider = BuildProvider(handler);

        var act = async () => await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModelProviderException>();
        thrown.Which.IsPermanent.Should().Be(expectedPermanent);
    }

    [Fact]
    public async Task GenerateAsync_throws_a_transient_exception_on_client_side_timeout()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new TaskCanceledException("timed out"));
        var provider = BuildProvider(handler);

        var act = async () => await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModelProviderException>();
        thrown.Which.IsPermanent.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_throws_a_transient_exception_on_network_interruption()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new HttpRequestException("connection reset"));
        var provider = BuildProvider(handler);

        var act = async () => await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModelProviderException>();
        thrown.Which.IsPermanent.Should().BeFalse();
    }

    /// <summary>
    /// Fase 12, Checkpoint 3, Decision Gate amendment — proves
    /// <see cref="AnthropicModelProvider"/>'s own catch/map logic in
    /// isolation (a real <see cref="Polly.CircuitBreaker.BrokenCircuitException"/>,
    /// simulated directly via <see cref="RecordingHttpMessageHandler.Throwing"/> —
    /// decoupled from whether the real resilience pipeline actually throws
    /// it, which <c>AnthropicCircuitBreakerTests</c> proves separately).
    /// Transient, exactly like Timeout/NetworkError above — the existing
    /// single application-level retry may still succeed once the breaker
    /// recovers.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_throws_a_transient_exception_when_the_circuit_breaker_is_open()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new Polly.CircuitBreaker.BrokenCircuitException());
        var provider = BuildProvider(handler);

        var act = async () => await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModelProviderException>();
        thrown.Which.IsPermanent.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateAsync_propagates_a_caller_initiated_cancellation_instead_of_reclassifying_it()
    {
        var handler = RecordingHttpMessageHandler.Throwing(new TaskCanceledException());
        var provider = BuildProvider(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await provider.GenerateAsync(SimpleRequest, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Missing credential -----------------------------------------------------

    [Fact]
    public async Task GenerateAsync_throws_a_permanent_exception_without_any_HTTP_call_when_no_API_key_is_configured()
    {
        var handler = RecordingHttpMessageHandler.Returning(JsonResponse(HttpStatusCode.OK, new { }));
        var provider = BuildProvider(handler, apiKey: null);

        var act = async () => await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ModelProviderException>();
        thrown.Which.IsPermanent.Should().BeTrue();
        handler.Requests.Should().BeEmpty("a missing API key must fail closed before any network call, never attempt one");
    }

    // ---- Secret safety --------------------------------------------------------

    [Fact]
    public async Task The_API_key_appears_only_in_the_x_api_key_header_never_in_the_request_body_or_URL()
    {
        var handler = RecordingHttpMessageHandler.Returning(
            JsonResponse(HttpStatusCode.OK, RespondToGuestToolUseResponse("Olá!", "pt-BR")));
        var provider = BuildProvider(handler);

        await provider.GenerateAsync(SimpleRequest, CancellationToken.None);

        var request = handler.Requests[0];
        request.Headers["x-api-key"].Should().Be(SentinelApiKey);
        request.Uri.ToString().Should().NotContain(SentinelApiKey);
        request.Body.Should().NotContain(SentinelApiKey);
    }
}
