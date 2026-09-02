namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// What a named policy does when Redis itself is unreachable — never a
/// single platform-wide choice (Fase 12, Checkpoint 3, Decision Gate item 2):
/// a security-sensitive policy (Authentication) must fail closed, while a
/// policy protecting mere availability/cost (Webhook, Tenant/Admin API, AI
/// cost guard) must fail open — Redis being down must never be the reason a
/// legitimate guest message or an already-authenticated tenant request is
/// rejected.
/// </summary>
public enum RateLimitFailureMode
{
    /// <summary>Redis unreachable → the request is allowed (rate limiting is best-effort, never a hard dependency).</summary>
    FailOpen,

    /// <summary>Redis unreachable → the request is rejected (used only where the cost of under-protecting outweighs the cost of unavailability).</summary>
    FailClosed,
}
