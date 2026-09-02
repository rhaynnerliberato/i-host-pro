namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate §3 —
/// the AI cost-guard policy, applied at the REAL orchestration boundary
/// (<see cref="ConversationMessageReceivedProcessor"/>, a Wolverine message
/// handler in <c>IHostPro.Worker</c>) rather than an HTTP endpoint, since the
/// AI Agent has none: it is triggered exclusively by
/// <c>ConversationMessageReceived</c>. Mirrors <c>IWebhookRateLimiter</c>'s
/// own shape (a thin Application-tier abstraction so this project — which
/// must not reference Infrastructure — can still use the shared Redis-backed
/// limiter). Never billing/plans/entitlements (explicitly out of scope,
/// reserved for the future SaaS Commercial Readiness audit) — purely a
/// technical guard against one tenant's traffic consuming unbounded LLM
/// capacity/cost at every other tenant's expense.
/// </summary>
public interface IAiAgentRateLimiter
{
    /// <returns><see langword="false"/> when <paramref name="tenantId"/> has exceeded its configured call budget for the current window.</returns>
    Task<bool> IsAllowedAsync(Guid tenantId, CancellationToken cancellationToken);
}
