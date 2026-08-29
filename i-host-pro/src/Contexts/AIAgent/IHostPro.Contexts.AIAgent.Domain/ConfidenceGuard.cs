namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// Fase 11, Checkpoint 2 governance resolution: Confidence is a normalized
/// <c>decimal?</c>, <c>0..1</c> inclusive when non-null. Shared by
/// <see cref="AgentSession.RecordInteraction"/> and
/// <see cref="AgentInteraction.CompleteSuccessfully"/> — never clamps, an
/// out-of-range value is an invariant violation the caller must never
/// silently swallow.
/// </summary>
internal static class ConfidenceGuard
{
    public static void EnsureValid(decimal? confidence)
    {
        if (confidence is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence), confidence,
                "Confidence must be between 0 and 1 (inclusive) when provided — never clamped.");
        }
    }
}
