namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Minimal result contract (mandate item 15, CP2; evolved by CP3's own
/// mandate item 10). No provider-specific stop-reason modeling
/// (<see cref="FinishReason"/> is a plain provider-neutral string).
///
/// <see cref="Confidence"/> is normalized <c>decimal?</c>, <c>0..1</c>
/// inclusive when non-null (Fase 11, Checkpoint 2 governance resolution —
/// <see langword="null"/> means the provider did not supply one). Range
/// validation is enforced where the value is actually persisted
/// (<c>AgentSession</c>/<c>AgentInteraction</c>'s own <c>ConfidenceGuard</c>),
/// not on this plain data-carrier record.
///
/// <see cref="ToolCallRequest"/> (Fase 11, Checkpoint 3) is set instead of a
/// final <see cref="Text"/> when the model wants a tool executed before it
/// produces its final answer — the orchestrator loop then executes the tool
/// and issues a second <see cref="IModelProvider.GenerateAsync"/> call with
/// the sanitized result appended as a <see cref="ModelMessageRole.Tool"/>
/// turn. <see langword="null"/> means the model produced (or is producing) a
/// final answer directly, no tool needed.
/// </summary>
public sealed record ModelResult(
    string Text,
    string? DetectedLanguage,
    string? Intent,
    decimal? Confidence,
    int InputTokens,
    int OutputTokens,
    string ModelName,
    string? FinishReason,
    ModelToolCallRequest? ToolCallRequest = null);

/// <summary>Provider-neutral tool-call request (Fase 11, Checkpoint 3) — Name + minimal Arguments only, never a provider-specific tool-call schema.</summary>
public sealed record ModelToolCallRequest(string ToolName, IReadOnlyDictionary<string, string>? Arguments);
