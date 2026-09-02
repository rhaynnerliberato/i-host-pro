namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate §§6-9
/// — closes the production gap left open since Fase 11 CP7
/// (<c>ContextWindowStrategy=FULL_CURRENT_CONVERSATION_CP7_MVP</c>, "sem
/// estratégia de truncamento para conversas longas em escala real"). Applies
/// ONLY to <see cref="AgentContextBuilder"/>'s conversation-history messages
/// — the system prompt (AI_AGENT_BEHAVIOR/policies/current-time/timezone
/// facts) is never subject to this budget, never truncated, per the approved
/// algorithm.
///
/// No production-grade token threshold is decided by this checkpoint —
/// <see cref="MaxHistoryTokens"/>'s default is a conservative dev/homologation
/// value; a real production number depends on the selected model's actual
/// context window and real pilot data (registered as
/// <c>ProductionContextBudgetFinalThresholdRequired=true</c> in the CP3
/// homologation document, mirroring <c>ProductionRateLimitThresholdsRequired</c>'s
/// own reasoning).
/// </summary>
public sealed class ContextBudgetOptions
{
    public const string SectionName = "AIAgent:ContextBudget";

    public bool Enabled { get; set; } = true;

    /// <summary>Applies only to <see cref="ModelRequest.Messages"/> — the system prompt is never counted against this budget.</summary>
    public int MaxHistoryTokens { get; set; } = 8000;

    /// <summary>
    /// No official Anthropic tokenizer is available in this stack without a
    /// new, otherwise-unneeded dependency (checked: none is already
    /// referenced anywhere in the solution) — this is a documented,
    /// deliberately conservative ESTIMATE (mandate §8: "não inventar precisão
    /// falsa"), never an exact count. A lower value yields a HIGHER estimated
    /// token count for the same text — conservative in the safety direction
    /// (truncates sooner rather than risking an under-count that lets more
    /// history through than the real model's tokenizer would allow).
    /// </summary>
    public double CharsPerTokenEstimate { get; set; } = 3.5;
}

/// <summary>
/// Thin abstraction so <see cref="AgentContextBuilder"/> (Application tier —
/// never references <c>Microsoft.Extensions.Options</c> directly, mirroring
/// this project's own established convention: no Application-tier class in
/// this codebase binds configuration options directly) can read the
/// configured budget. The real implementation (<c>ContextBudgetPolicy</c>,
/// Infrastructure) wraps <c>IOptions&lt;ContextBudgetOptions&gt;</c>.
/// </summary>
public interface IContextBudgetPolicy
{
    ContextBudgetOptions Current { get; }
}
