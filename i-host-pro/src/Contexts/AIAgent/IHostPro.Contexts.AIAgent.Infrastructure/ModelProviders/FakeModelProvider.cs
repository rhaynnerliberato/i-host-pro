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
/// full loop end-to-end without ever re-triggering the same tool call. The
/// same marker optionally carries <c>key=value</c> arguments after a second
/// <c>:</c> (e.g. <c>[FAKE_MODEL_TOOL_CALL:RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]</c>,
/// multiple pairs separated by <c>;</c>) — CP4's write Tools need a
/// model-supplied argument the way CP3's Read Tools never did.
///
/// Fase 11, Checkpoint 4 extends this further with two more deterministic
/// markers, mirroring the exact same convention: <see cref="ConfirmTriggerMarker"/>/
/// <see cref="CancelTriggerMarker"/> — checked under the same "not a Tool-role
/// turn" guard — set <see cref="ModelResult.ConfirmationIntent"/> to
/// <see langword="true"/>/<see langword="false"/> respectively, with
/// <see cref="ModelResult.ToolCallRequest"/> left <see langword="null"/> (the
/// orchestrator alone decides what to do with a confirm/cancel classification
/// — this class only classifies the guest's own message, never authorizes
/// anything).
///
/// Fase 11, Checkpoint 5 adds three more deterministic markers:
/// <see cref="TransientFailureTriggerMarker"/> throws
/// <see cref="ModelProviderException"/> only the FIRST time a given message
/// content is seen by THIS instance, then falls through to a normal response
/// on every subsequent call with the identical content — proving the
/// orchestrator's one-controlled-retry policy end-to-end without any DI
/// reconfiguration (mirrors <see cref="FailureTriggerMarker"/>'s own
/// always-throws convention, but bounded). This relies on
/// <see cref="FakeModelProvider"/> being registered <c>Scoped</c> (one
/// instance per inbound message's own processing scope) — the attempt
/// counter is instance-level and keyed by the exact message content, so it
/// never leaks across different inbound messages or different Call#1/Call#2
/// contents within the same interaction. <see cref="UnsupportedRequestTriggerMarker"/>/
/// <see cref="HumanHandoffTriggerMarker"/> classify the guest's message via
/// <see cref="ModelResult.Intent"/> (never a Tool call) — the orchestrator
/// itself does nothing special with these; they are ordinary final answers
/// whose <c>Intent</c> is simply auditable in <c>AgentInteraction.Intent</c>.
///
/// Fase 11, Checkpoint 6 adds <see cref="IntentTriggerPrefix"/> — a generic
/// marker carrying any intent value (e.g. <c>[FAKE_MODEL_INTENT:refund]</c>),
/// so every restricted-intent reason
/// <see cref="Application.IAgentHumanHandoffReasonClassifier"/> maps
/// (refund/accident/police/negotiation/severe_damage/serious_complaint/
/// aggressive_behavior/low_confidence/integration_failure) is provable
/// without a dedicated named marker per reason — mirrors
/// <see cref="ConfidenceMarkerPrefix"/>'s own carries-a-value convention.
/// <see cref="HumanHandoffTriggerMarker"/> remains a separate, named marker
/// (its own reason, <c>ExplicitHumanRequest</c>, predates this generic
/// mechanism from Checkpoint 5) — both ultimately just set
/// <see cref="ModelResult.Intent"/>, so either spelling works for that one
/// reason.
/// </remarks>
public sealed class FakeModelProvider : IModelProvider
{
    public const string FailureTriggerMarker = "[FAKE_MODEL_FAILURE]";
    public const string TransientFailureTriggerMarker = "[FAKE_MODEL_TRANSIENT_FAILURE]";
    public const string ConfidenceMarkerPrefix = "[FAKE_MODEL_CONFIDENCE:";
    public const string ToolCallTriggerPrefix = "[FAKE_MODEL_TOOL_CALL:";
    public const string ConfirmTriggerMarker = "[FAKE_MODEL_CONFIRM]";
    public const string CancelTriggerMarker = "[FAKE_MODEL_CANCEL]";
    public const string UnsupportedRequestTriggerMarker = "[FAKE_MODEL_UNSUPPORTED]";
    public const string HumanHandoffTriggerMarker = "[FAKE_MODEL_HUMAN_HANDOFF]";
    public const string IntentTriggerPrefix = "[FAKE_MODEL_INTENT:";

    private const string ModelNameValue = "fake-model-v1";
    private const string UnsupportedRequestIntent = "unsupported_request";
    private const string HumanHandoffRequestedIntent = "human_handoff_requested";
    private const string UnsupportedRequestResponseText =
        "No momento não consigo ajudar com esse tipo de solicitação. Posso encaminhar para nossa equipe, se preferir.";
    private const string HumanHandoffResponseText =
        "Identifiquei seu pedido para falar com uma pessoa da nossa equipe. Assim que possível, alguém dará continuidade ao seu atendimento.";
    private const string GenericIntentResponseText = "[FAKE MODEL RESPONSE] intent classified.";

    private static readonly Regex ConfidenceMarkerPattern = new(
        @"\[FAKE_MODEL_CONFIDENCE:(?<value>[0-9]*\.?[0-9]+)\]", RegexOptions.Compiled);

    private static readonly Regex ToolCallMarkerPattern = new(
        @"\[FAKE_MODEL_TOOL_CALL:(?<name>[A-Za-z0-9_]+)(?::(?<args>[^\]]+))?\]", RegexOptions.Compiled);

    private static readonly Regex IntentMarkerPattern = new(
        @"\[FAKE_MODEL_INTENT:(?<intent>[a-z_]+)\]", RegexOptions.Compiled);

    private readonly ILogger<FakeModelProvider> _logger;
    private readonly Dictionary<string, int> _transientFailureAttemptsByContent = new();

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

        if (lastContent.Contains(TransientFailureTriggerMarker, StringComparison.Ordinal))
        {
            var attempts = _transientFailureAttemptsByContent.GetValueOrDefault(lastContent) + 1;
            _transientFailureAttemptsByContent[lastContent] = attempts;

            if (attempts == 1)
            {
                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic TRANSIENT controlled failure triggered (attempt {Attempt})", attempts);
                throw new ModelProviderException("FakeModelProvider: deterministic transient failure triggered by TransientFailureTriggerMarker (attempt 1).");
            }

            _logger.LogInformation(
                "[FAKE Model Provider — Development/Test only, no real model called] TransientFailureTriggerMarker already failed once for this content — succeeding (attempt {Attempt})", attempts);
        }

        var inputTokens = request.Messages.Sum(m => m.Content.Length);
        var confidence = ExtractConfidenceMarker(lastContent);

        if (lastMessage?.Role != ModelMessageRole.Tool)
        {
            if (lastContent.Contains(UnsupportedRequestTriggerMarker, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic unsupported-request intent classified");

                return Task.FromResult(new ModelResult(
                    Text: UnsupportedRequestResponseText,
                    DetectedLanguage: "pt-BR",
                    Intent: UnsupportedRequestIntent,
                    Confidence: confidence,
                    InputTokens: inputTokens,
                    OutputTokens: UnsupportedRequestResponseText.Length,
                    ModelName: ModelNameValue,
                    FinishReason: "stop"));
            }

            if (lastContent.Contains(HumanHandoffTriggerMarker, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic human-handoff-requested intent classified");

                return Task.FromResult(new ModelResult(
                    Text: HumanHandoffResponseText,
                    DetectedLanguage: "pt-BR",
                    Intent: HumanHandoffRequestedIntent,
                    Confidence: confidence,
                    InputTokens: inputTokens,
                    OutputTokens: HumanHandoffResponseText.Length,
                    ModelName: ModelNameValue,
                    FinishReason: "stop"));
            }

            var intentMatch = IntentMarkerPattern.Match(lastContent);
            if (intentMatch.Success)
            {
                var intent = intentMatch.Groups["intent"].Value;

                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic intent classified: {Intent}", intent);

                return Task.FromResult(new ModelResult(
                    Text: GenericIntentResponseText,
                    DetectedLanguage: "pt-BR",
                    Intent: intent,
                    Confidence: confidence,
                    InputTokens: inputTokens,
                    OutputTokens: GenericIntentResponseText.Length,
                    ModelName: ModelNameValue,
                    FinishReason: "stop"));
            }

            if (lastContent.Contains(ConfirmTriggerMarker, StringComparison.Ordinal)
                || lastContent.Contains(CancelTriggerMarker, StringComparison.Ordinal))
            {
                var confirmationIntent = lastContent.Contains(ConfirmTriggerMarker, StringComparison.Ordinal);

                _logger.LogInformation(
                    "[FAKE Model Provider — Development/Test only, no real model called] deterministic confirmation intent classified: {ConfirmationIntent}",
                    confirmationIntent);

                return Task.FromResult(new ModelResult(
                    Text: string.Empty,
                    DetectedLanguage: "pt-BR",
                    Intent: null,
                    Confidence: confidence,
                    InputTokens: inputTokens,
                    OutputTokens: 0,
                    ModelName: ModelNameValue,
                    FinishReason: "confirmation_intent",
                    ConfirmationIntent: confirmationIntent));
            }

            var toolCallMatch = ToolCallMarkerPattern.Match(lastContent);
            if (toolCallMatch.Success)
            {
                var toolName = toolCallMatch.Groups["name"].Value;
                var arguments = ParseToolCallArguments(toolCallMatch.Groups["args"]);

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
                    ToolCallRequest: new ModelToolCallRequest(toolName, arguments)));
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

    /// <summary>Parses the optional <c>key1=val1;key2=val2</c> argument fragment of a tool-call marker (Fase 11, Checkpoint 4) — <see langword="null"/> when the marker carried no arguments.</summary>
    private static IReadOnlyDictionary<string, string>? ParseToolCallArguments(Group argsGroup)
    {
        if (!argsGroup.Success)
            return null;

        var arguments = new Dictionary<string, string>();
        foreach (var pair in argsGroup.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            arguments[pair[..separatorIndex]] = pair[(separatorIndex + 1)..];
        }

        return arguments;
    }
}
