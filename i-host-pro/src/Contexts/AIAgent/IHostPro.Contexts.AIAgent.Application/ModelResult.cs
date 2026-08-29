namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Minimal result contract (mandate item 15) — only the fields the mandate
/// itself lists as effectively necessary. No provider-specific stop-reason
/// modeling (<see cref="FinishReason"/> is a plain provider-neutral string).
///
/// <see cref="Confidence"/> is normalized <c>decimal?</c>, <c>0..1</c>
/// inclusive when non-null (Fase 11, Checkpoint 2 governance resolution —
/// <see langword="null"/> means the provider did not supply one). Range
/// validation is enforced where the value is actually persisted
/// (<c>AgentSession</c>/<c>AgentInteraction</c>'s own <c>ConfidenceGuard</c>),
/// not on this plain data-carrier record.
/// </summary>
public sealed record ModelResult(
    string Text,
    string? DetectedLanguage,
    string? Intent,
    decimal? Confidence,
    int InputTokens,
    int OutputTokens,
    string ModelName,
    string? FinishReason);
