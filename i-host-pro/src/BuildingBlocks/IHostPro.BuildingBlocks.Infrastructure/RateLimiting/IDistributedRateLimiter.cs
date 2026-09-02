namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// Host-agnostic rate-limit check — deliberately never tied to
/// <c>Microsoft.AspNetCore.RateLimiting</c>/<c>HttpContext</c>, since the AI
/// cost-guard policy is enforced from a Wolverine message handler in
/// <c>IHostPro.Worker</c>, which has no HTTP request at all (Fase 12, CP3,
/// Decision Gate item 3 — the AI Agent is triggered by
/// <c>ConversationMessageReceived</c>, never an HTTP endpoint). ASP.NET
/// Core's own rate-limiting middleware wraps this same service for the HTTP
/// policies (see <c>IHostPro.Api</c>'s composition root) via a custom
/// <c>PartitionedRateLimiter</c>.
/// </summary>
public interface IDistributedRateLimiter
{
    /// <summary>
    /// Atomically increments the counter for (<paramref name="policyName"/>, <paramref name="partitionKey"/>)
    /// and returns whether this call is within the policy's configured limit.
    /// A policy name that isn't configured in <see cref="RateLimitingOptions.Policies"/>
    /// is treated as unlimited (always allowed) — callers only invoke this for
    /// policies they've deliberately configured.
    /// </summary>
    Task<RateLimitDecision> CheckAsync(string policyName, string partitionKey, CancellationToken cancellationToken);
}

/// <param name="Allowed">Whether the caller may proceed.</param>
/// <param name="RetryAfter">Set only when <paramref name="Allowed"/> is <see langword="false"/> and the wait time is known — the remaining time-to-live of the current window.</param>
/// <param name="FailedOpen">Set when Redis itself was unreachable and the policy's <see cref="RateLimitFailureMode.FailOpen"/> is why <paramref name="Allowed"/> is <see langword="true"/> — surfaced so callers/metrics can distinguish "allowed because under limit" from "allowed because the limiter degraded."</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan? RetryAfter = null, bool FailedOpen = false)
{
    public static RateLimitDecision Allow() => new(true);

    public static RateLimitDecision Deny(TimeSpan retryAfter) => new(false, retryAfter);
}
