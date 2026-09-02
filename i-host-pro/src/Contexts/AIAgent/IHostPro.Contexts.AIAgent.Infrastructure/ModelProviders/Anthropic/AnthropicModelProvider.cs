using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Real <see cref="IModelProvider"/> implementation for the Anthropic
/// Messages API (Fase 11, Checkpoint 7 — ADR-009). REST via
/// <see cref="IHttpClientFactory"/> only, no SDK. Anthropic-specific
/// vocabulary/DTOs live ONLY in this file and <see cref="AnthropicDtos"/> —
/// never in <see cref="ModelRequest"/>/<see cref="ModelResult"/> or any Tool
/// contract (mandate item 6).
///
/// Structured metadata (mandate item 24/25): every call forces the model to
/// respond by calling a tool — either a real business Tool from
/// <see cref="ModelRequest.AvailableTools"/>, or the private, non-business
/// <c>respond_to_guest</c> control tool this class alone defines. That
/// control tool is never an <see cref="IAgentTool"/>, never appears in the
/// business Tool catalogue, never executes anything — its own <c>input</c>
/// (message/language/intent/confirmation_intent) maps directly onto
/// <see cref="ModelResult"/>'s already-existing fields. This avoids ever
/// parsing free text with a regex for a business-significant decision
/// (mandate item 24), and keeps the existing one-or-two-call orchestration
/// shape intact: Call#1 offers business Tools + <c>respond_to_guest</c> with
/// <c>tool_choice=any</c>; Call#2 (detected by the last message already
/// being a <see cref="ModelMessageRole.Tool"/> turn — mirrors
/// <c>FakeModelProvider</c>'s own gate) forces <c>respond_to_guest</c> alone,
/// matching Checkpoint 3's "no multi-hop" rule.
/// </summary>
public sealed class AnthropicModelProvider : IModelProvider
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client — see <c>AIAgentModuleExtensions</c> for its registration (base address, timeout).</summary>
    public const string HttpClientName = "AIAgent.Anthropic";

    /// <summary>
    /// Fase 12, Checkpoint 2 (Observability Finalization, Documento 21 §16 —
    /// "IA" is one of the explicitly required metric categories). Registered
    /// with <c>.AddMeter("IHostPro.AIAgent")</c> only in <c>IHostPro.Worker</c>
    /// (the only process that ever constructs this class) — never in
    /// IHostPro.Api, which never calls a model provider. Every tag below is
    /// a bounded, low-cardinality enum (provider name, model name, a fixed
    /// outcome string, or "input"/"output") — never tenant/reservation/
    /// conversation id, phone, or any other unbounded/PII-adjacent value
    /// (mandate item 13's own explicit prohibition).
    /// </summary>
    private static readonly Meter Meter = new("IHostPro.AIAgent");
    private static readonly Counter<long> ModelCallsCounter = Meter.CreateCounter<long>("ai_agent.model_calls");
    private static readonly Counter<long> TokensCounter = Meter.CreateCounter<long>("ai_agent.tokens");
    private static readonly Counter<double> CostCounter = Meter.CreateCounter<double>("ai_agent.cost_usd");

    private const string RespondToGuestToolName = "respond_to_guest";
    private const string ApiKeyHeaderName = "x-api-key";
    private const string ApiVersionHeaderName = "anthropic-version";

    /// <summary>The fixed, closed set of restricted-intent values <see cref="IAgentHumanHandoffReasonClassifier"/> recognizes, plus <c>unsupported_request</c> — mirrors both exactly, never invented independently.</summary>
    private static readonly string[] KnownIntentValues =
    [
        "human_handoff_requested", "refund", "accident", "police", "negotiation",
        "severe_damage", "serious_complaint", "aggressive_behavior", "low_confidence",
        "integration_failure", "unsupported_request",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnthropicCredentialProvider _credentialProvider;
    private readonly IOptions<AnthropicOptions> _options;
    private readonly ILogger<AnthropicModelProvider> _logger;

    public string ProviderName => "Anthropic";

    public string ModelName => _options.Value.Model;

    public AnthropicModelProvider(
        IHttpClientFactory httpClientFactory, IAnthropicCredentialProvider credentialProvider,
        IOptions<AnthropicOptions> options, ILogger<AnthropicModelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialProvider = credentialProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<ModelResult> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var stopwatch = Stopwatch.StartNew();

        var apiKey = await _credentialProvider.GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("AIAgent Anthropic call skipped: no API key configured");
            throw new ModelProviderException("Anthropic API key is not configured.", isPermanent: true);
        }

        // Call#2 (mandate item 24/25): the last message is already the
        // sanitized Tool-role turn ConversationMessageReceivedProcessor
        // appends after a real/synthetic Tool ran — the model never gets a
        // second chance to request a different Tool (Checkpoint 3's "no
        // multi-hop" rule), it must produce its final answer now.
        var isSecondCall = request.Messages.Count > 0 && request.Messages[^1].Role == ModelMessageRole.Tool;

        var allTools = BuildToolDefinitions(request.AvailableTools);
        var requestBody = new AnthropicRequestBody
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = request.SystemPrompt,
            Messages = ToAnthropicMessages(request.Messages),
            Tools = isSecondCall ? [allTools.Single(t => t.Name == RespondToGuestToolName)] : allTools,
            ToolChoice = isSecondCall ? AnthropicToolChoice.ForceTool(RespondToGuestToolName) : AnthropicToolChoice.Any(),
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AnthropicOptions.MessagesPath)
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        httpRequest.Headers.Add(ApiVersionHeaderName, options.ApiVersion);

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogOutcome("Timeout", stopwatch, null, null, null);
            throw new ModelProviderException("Anthropic request timed out.");
        }
        catch (HttpRequestException)
        {
            LogOutcome("NetworkError", stopwatch, null, null, null);
            throw new ModelProviderException("Anthropic network error.");
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            // Fase 12, Checkpoint 3 — the circuit breaker rejected this call
            // locally, without a real HTTP attempt (repeated recent failures
            // already exceeded the configured threshold). Transient, exactly
            // like Timeout/NetworkError above — the existing single
            // application-level retry (ConversationMessageReceivedProcessor)
            // may still succeed once the breaker recovers.
            LogOutcome("CircuitOpen", stopwatch, null, null, null);
            throw new ModelProviderException("Anthropic circuit breaker is open.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var permanent = IsPermanentFailure(response.StatusCode);
            var errorType = TryParseErrorType(responseBody);
            LogOutcome($"Http{(int)response.StatusCode}", stopwatch, null, null, null);
            throw new ModelProviderException(
                $"Anthropic returned {(int)response.StatusCode} ({errorType ?? "unknown"}).", isPermanent: permanent);
        }

        var parsed = JsonSerializer.Deserialize<AnthropicResponseBody>(responseBody, JsonOptions);
        if (parsed?.Content is null || parsed.Usage is null)
        {
            LogOutcome("MalformedResponse", stopwatch, null, null, null);
            throw new ModelProviderException("Anthropic returned a malformed response.");
        }

        var toolUseBlock = parsed.Content.FirstOrDefault(b => b.Type == "tool_use");
        if (toolUseBlock?.Name is null)
        {
            // A real, correctly-configured tool_choice (any/forced) should
            // never let this happen — treated as transient rather than
            // permanent, since it is not a documented, reproducible failure
            // mode (mandate item 44 only names auth/contract/model errors as
            // permanent).
            LogOutcome("NoToolUse", stopwatch, null, null, null);
            throw new ModelProviderException("Anthropic did not return a tool_use block despite a forced tool choice.");
        }

        var (estimatedCostUsd, costPricingReference) = ComputeCost(parsed.Usage, options.Pricing);
        var modelName = parsed.Model ?? options.Model;

        var result = toolUseBlock.Name == RespondToGuestToolName
            ? MapRespondToGuest(toolUseBlock, parsed, modelName, estimatedCostUsd, costPricingReference)
            : MapBusinessToolCall(toolUseBlock, parsed, modelName, estimatedCostUsd, costPricingReference);

        LogOutcome("Success", stopwatch, parsed.Usage.InputTokens, parsed.Usage.OutputTokens, estimatedCostUsd);
        return result;
    }

    /// <summary>
    /// Guest/Agent map to Anthropic's own user/assistant roles with a plain
    /// text block. A <see cref="ModelMessageRole.Tool"/> turn — the
    /// orchestrator's own ephemeral, already-sanitized Tool result, never
    /// part of the real persisted conversation — is sent as an ordinary
    /// user-role text block with an explicit prefix, never Anthropic's
    /// native <c>tool_result</c> block: this provider is stateless per call
    /// (mandate item 39's own IHttpClientFactory pattern implies no
    /// server-side conversation state kept between calls either), and the
    /// neutral <see cref="ModelMessage"/> shape carries no
    /// <c>tool_use_id</c> to link a real tool_result to — reconstructing one
    /// would require leaking an Anthropic-specific concept into
    /// <see cref="ModelMessage"/>, which mandate item 6 forbids.
    /// </summary>
    private static IReadOnlyList<AnthropicRequestMessage> ToAnthropicMessages(IReadOnlyList<ModelMessage> messages) =>
        messages.Select(m => new AnthropicRequestMessage
        {
            Role = m.Role == ModelMessageRole.Agent ? "assistant" : "user",
            Content = [new AnthropicRequestContentBlock
            {
                Text = m.Role == ModelMessageRole.Tool ? $"[Resultado do sistema] {m.Content}" : m.Content,
            }],
        }).ToList();

    private static IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions(IReadOnlyList<AgentToolDescriptor>? availableTools)
    {
        var definitions = new List<AnthropicToolDefinition>();

        if (availableTools is not null)
        {
            // A permissive schema (no fixed property list) — the model's own
            // arguments are only ever advisory; every Confirmable Tool
            // re-validates/sanitizes them server-side regardless (defense in
            // depth already established since Checkpoint 4), so a strict
            // per-Tool schema is not required for CP7's own scope.
            definitions.AddRange(availableTools.Select(tool => new AnthropicToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                },
            }));
        }

        definitions.Add(BuildRespondToGuestToolDefinition());
        return definitions;
    }

    private static AnthropicToolDefinition BuildRespondToGuestToolDefinition() => new()
    {
        Name = RespondToGuestToolName,
        Description =
            "Envie a resposta final ao hóspede. Utilize esta ferramenta sempre que estiver pronto para responder " +
            "diretamente, em vez de texto livre — inclusive quando a resposta apenas reconhece uma solicitação que " +
            "será encaminhada.",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A resposta final em linguagem natural, no idioma do hóspede.",
                },
                ["language"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Idioma da mensagem do hóspede, no formato BCP-47 (ex.: pt-BR, en-US).",
                },
                ["intent"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(KnownIntentValues.Select(value => (JsonNode)value).ToArray()),
                    ["description"] =
                        "Preencha somente quando a mensagem do hóspede se enquadrar claramente em um destes " +
                        "casos restritos; omita este campo em qualquer outra situação.",
                },
                ["confirmation_intent"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "true se esta mensagem confirma uma ação pendente proposta anteriormente na conversa; " +
                        "false se a cancela; omita se não for uma resposta de confirmação.",
                },
            },
            ["required"] = new JsonArray("message", "language"),
        },
    };

    private static ModelResult MapRespondToGuest(
        AnthropicResponseContentBlock toolUseBlock, AnthropicResponseBody parsed, string modelName,
        decimal estimatedCostUsd, string costPricingReference)
    {
        var input = toolUseBlock.Input ?? [];
        var message = TryGetString(input, "message") ?? string.Empty;
        var language = TryGetString(input, "language");
        var intent = TryGetString(input, "intent");
        var confirmationIntent = TryGetBool(input, "confirmation_intent");

        return new ModelResult(
            Text: message,
            DetectedLanguage: language,
            Intent: intent,
            Confidence: null,
            InputTokens: parsed.Usage!.InputTokens,
            OutputTokens: parsed.Usage.OutputTokens,
            ModelName: modelName,
            FinishReason: parsed.StopReason,
            ToolCallRequest: null,
            ConfirmationIntent: confirmationIntent,
            EstimatedCostUsd: estimatedCostUsd,
            CostPricingReference: costPricingReference);
    }

    private static ModelResult MapBusinessToolCall(
        AnthropicResponseContentBlock toolUseBlock, AnthropicResponseBody parsed, string modelName,
        decimal estimatedCostUsd, string costPricingReference)
    {
        IReadOnlyDictionary<string, string>? arguments = toolUseBlock.Input is null
            ? null
            : toolUseBlock.Input.ToDictionary(kv => kv.Key, kv => JsonNodeToArgumentString(kv.Value));

        return new ModelResult(
            Text: string.Empty,
            DetectedLanguage: null,
            Intent: null,
            Confidence: null,
            InputTokens: parsed.Usage!.InputTokens,
            OutputTokens: parsed.Usage.OutputTokens,
            ModelName: modelName,
            FinishReason: parsed.StopReason,
            ToolCallRequest: new ModelToolCallRequest(toolUseBlock.Name!, arguments),
            ConfirmationIntent: null,
            EstimatedCostUsd: estimatedCostUsd,
            CostPricingReference: costPricingReference);
    }

    private static string? TryGetString(JsonObject input, string propertyName) =>
        input.TryGetPropertyValue(propertyName, out var node) && node is not null ? node.GetValue<string>() : null;

    private static bool? TryGetBool(JsonObject input, string propertyName) =>
        input.TryGetPropertyValue(propertyName, out var node) && node is not null ? node.GetValue<bool>() : null;

    private static string JsonNodeToArgumentString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var stringValue) ? stringValue : node?.ToString() ?? string.Empty;

    /// <summary>Mandate item 37/40 — real token usage times configured pricing, computed here (Infrastructure), never in Domain/Application (mandate item 38).</summary>
    private static (decimal EstimatedCostUsd, string CostPricingReference) ComputeCost(AnthropicUsage usage, AnthropicPricingOptions pricing)
    {
        var cost = (usage.InputTokens / 1_000_000m * pricing.InputUsdPerMillionTokens)
            + (usage.OutputTokens / 1_000_000m * pricing.OutputUsdPerMillionTokens);
        return (cost, pricing.Reference);
    }

    /// <summary>Mandate item 44/47 — 400/401/403/404 are permanent (a retry cannot fix a contract violation, invalid credentials, or an unsupported model id); everything else (429, 5xx) is transient.</summary>
    private static bool IsPermanentFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => true,
        HttpStatusCode.Unauthorized => true,
        HttpStatusCode.Forbidden => true,
        HttpStatusCode.NotFound => true,
        _ => false,
    };

    private static string? TryParseErrorType(string responseBody)
    {
        try
        {
            return JsonSerializer.Deserialize<AnthropicErrorResponse>(responseBody, JsonOptions)?.Error?.Type;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sanitized metadata only (mandate item 40/41/42/49/50) — never the API
    /// key, never the system prompt, never message content, never the raw
    /// Anthropic request/response body.
    /// </summary>
    private void LogOutcome(string result, Stopwatch stopwatch, int? inputTokens, int? outputTokens, decimal? estimatedCostUsd)
    {
        var level = result == "Success" ? LogLevel.Information : LogLevel.Warning;

        _logger.Log(
            level,
            "AIAgent Anthropic call {Result} for model {Model} (InputTokens {InputTokens}, OutputTokens {OutputTokens}, " +
            "EstimatedCostUsd {EstimatedCostUsd}, {DurationMs}ms)",
            result, ModelName, inputTokens, outputTokens, estimatedCostUsd, stopwatch.ElapsedMilliseconds);

        // Fase 12, Checkpoint 2 — the single choke point every code path
        // above already funnels through, success or failure alike, so
        // "calls"/"errors" (both covered by ModelCallsCounter's own
        // "outcome" tag — a non-"Success" outcome IS the error) and
        // "tokens"/"cost" (recorded only on Success, mirroring
        // ModelResult's own "null = not applicable" convention) never need a
        // second instrumentation point anywhere else in this class.
        ModelCallsCounter.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("model", ModelName),
            new KeyValuePair<string, object?>("outcome", result));

        if (inputTokens is int input)
            TokensCounter.Add(input,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("model", ModelName),
                new KeyValuePair<string, object?>("direction", "input"));

        if (outputTokens is int output)
            TokensCounter.Add(output,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("model", ModelName),
                new KeyValuePair<string, object?>("direction", "output"));

        if (estimatedCostUsd is decimal cost)
            CostCounter.Add((double)cost,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("model", ModelName));
    }
}
