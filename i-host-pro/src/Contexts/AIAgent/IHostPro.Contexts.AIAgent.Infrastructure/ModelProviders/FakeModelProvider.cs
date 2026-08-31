using System.Globalization;
using System.Text.RegularExpressions;
using IHostPro.Contexts.AIAgent.Application;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;

/// <inheritdoc cref="IModelProvider"/>
/// <remarks>
/// Fase 11, Checkpoint 2's ONLY implementation of <see cref="IModelProvider"/>
/// — a deterministic development/test double, never a real Anthropic client
/// (mirrors <c>FakePixProvider</c>/<c>FakeWhatsAppConnector</c>'s own
/// precedent exactly: Documento 19 §28, every Connector must be
/// substitutable by a fake; real Anthropic integration is Checkpoint 7's
/// scope, never built speculatively). Zero network calls, zero real
/// credentials.
///
/// Deterministic by design (mandate item 16 — "input conhecido → resposta
/// conhecida", explicitly NOT a "mini LLM"): the response text, token
/// counts, and outcome are all pure functions of <see cref="ModelRequest.Messages"/>'s
/// own content — the SAME request always produces the SAME result, with two
/// documented exceptions: a message whose content contains
/// <see cref="FailureTriggerMarker"/> deterministically throws
/// <see cref="ModelProviderException"/> (mandate item 35 — proves the
/// "Fake provider controlled failure" path end-to-end without any DI
/// reconfiguration), and a message containing <see cref="ConfidenceMarkerPrefix"/>
/// followed by a decimal value and <c>]</c> deterministically returns that
/// exact value as <see cref="ModelResult.Confidence"/> (governance
/// resolution item 9 — "testes específicos podem configurar confidence
/// conhecida... não criar lógica probabilística").
///
/// <see cref="ModelResult.DetectedLanguage"/> is a fixed <c>"pt-BR"</c> (no
/// real language detection exists yet — mandate item 31 lists this as an
/// acceptable deterministic test value) and <see cref="ModelResult.Intent"/>
/// is deliberately <see langword="null"/> — CP2 defines no intent catalog
/// (mandate item 22/32) to populate it with.
///
/// Fase 11, Checkpoint 3 extends this with a deterministic two-call tool
/// loop, mirroring <see cref="FailureTriggerMarker"/>/<see cref="ConfidenceMarkerPrefix"/>'s
/// own marker convention: a message containing <see cref="ToolCallTriggerPrefix"/>
/// followed by a tool name and <c>]</c> — as long as it is not itself a
/// <see cref="ModelMessageRole.Tool"/> turn — deterministically returns a
/// <see cref="ModelResult.ToolCallRequest"/> instead of final text. Once the
/// orchestrator appends the executed tool's sanitized result as the new last
/// message (<see cref="ModelMessageRole.Tool"/>), that same marker is no
/// longer the last message's content, so the next call falls through to a
/// final deterministic answer that echoes the tool result back — proving the
/// full loop end-to-end without ever re-triggering the same tool call.
/// </remarks>
public sealed class FakeModelProvider : IModelProvider
{
    public const string FailureTriggerMarker = "[FAKE_MODEL_FAILURE]";
    public const string ConfidenceMarkerPrefix = "[FAKE_MODEL_CONFIDENCE:";
    public const string ToolCallTriggerPrefix = "[FAKE_MODEL_TOOL_CALL:";
    private const string ModelNameValue = "fake-model-v1";

    private static readonly Regex ConfidenceMarkerPattern = new(
        @"\[FAKE_MODEL_CONFIDENCE:(?<value>[0-9]*\.?[0-9]+)\]", RegexOptions.Compiled);

    private static readonly Regex ToolCallMarkerPattern = new(
        @"\[FAKE_MODEL_TOOL_CALL:(?<name>[A-Za-z0-9_]+)\]", RegexOptions.Compiled);

    private readonly ILogger<FakeModelProvider> _logger;

    public string ProviderName => "Fake";

    public string ModelName => ModelNameValue;

    public FakeModelProvider(ILogger<FakeModelProvider> logger) => _logger = logger;

    public Task<ModelResult> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var lastMessage = request.Messages.Count > 0 ? request.Messages[^1] : null;
        var lastContent = lastMessage?.Content ?? string.Empty;

        if (lastContent.Contains(FailureTriggerMarker, StringComparison.Ordinal))
        {
            _logger.LogInformation("[FAKE Model Provider — Development/Test only, no real model called] deterministic controlled failure triggered");
            throw new ModelProviderException("FakeModelProvider: deterministic controlled failure triggered by FailureTriggerMarker.");
        }

        var inputTokens = request.Messages.Sum(m => m.Content.Length);
        var confidence = ExtractConfidenceMarker(lastContent);

        if (lastMessage?.Role != ModelMessageRole.Tool)
        {
            var toolCallMatch = ToolCallMarkerPattern.Match(lastContent);
            if (toolCallMatch.Success)
            {
                var toolName = toolCallMatch.Groups["name"].Value;

                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic tool call requested: {ToolName}",
                    toolName);

                return Task.FromResult(new ModelResult(
                    Text: string.Empty,
                    DetectedLanguage: "pt-BR",
                    Intent: null,
                    Confidence: confidence,
                    InputTokens: inputTokens,
                    OutputTokens: 0,
                    ModelName: ModelNameValue,
                    FinishReason: "tool_call",
                    ToolCallRequest: new ModelToolCallRequest(toolName, null)));
            }
        }

        var responseText = lastMessage?.Role == ModelMessageRole.Tool
            ? $"[FAKE MODEL RESPONSE] tool result considered: {lastMessage.Content}"
            : $"[FAKE MODEL RESPONSE] {request.Messages.Count} message(s) considered.";

        // Never logs request.Messages content — only counts, mirroring
        // FakeWhatsAppConnector/FakePixProvider's own "never log
        // PII/business content" discipline.
        _logger.LogInformation(
            "[FAKE Model Provider — Development/Test only, no real model called] generated a deterministic response from {MessageCount} message(s)",
            request.Messages.Count);

        return Task.FromResult(new ModelResult(
            Text: responseText,
            DetectedLanguage: "pt-BR",
            Intent: null,
            Confidence: confidence,
            InputTokens: inputTokens,
            OutputTokens: responseText.Length,
            ModelName: ModelNameValue,
            FinishReason: "stop"));
    }

    private static decimal? ExtractConfidenceMarker(string content)
    {
        var match = ConfidenceMarkerPattern.Match(content);
        return match.Success
            ? decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            : null;
    }
}
