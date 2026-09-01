namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The single, typed synchronous query port another Bounded Context may use
/// to resolve the effective <c>AI_AGENT_BEHAVIOR</c> policy (Fase 11,
/// Checkpoint 7 — mirrors <see cref="IEarlyCheckInPolicyReader"/> exactly).
/// Implemented ONLY in <c>Configuration.Infrastructure</c> — a consumer may
/// reference this contract, never <c>Configuration.Application</c>/
/// <c>Infrastructure</c>/<c>Api</c>, and never <c>ConfigurationDbContext</c>/
/// the <c>configuration</c> schema directly. This is the AI Agent's Context
/// Builder's own use of Architecture Principles' Exceção 1 (synchronous
/// consultation of Configuration &amp; Policy) — no new synchronous exception
/// was needed.
/// </summary>
public interface IAiAgentBehaviorPolicyReader
{
    /// <summary>
    /// Resolves the effective value following PROPERTY → TENANT → GLOBAL
    /// precedence (mandate item 14 — GROUP/CONDOMINIUM scopes are not
    /// implemented; the Fase 5, Incremento 1 decision to keep the policy
    /// hierarchy at 3 levels remains sovereign). When <paramref name="propertyId"/>
    /// is <c>null</c>, only TENANT → GLOBAL are considered. Throws
    /// <see cref="PolicyEngineUnavailableException"/> when the engine cannot
    /// answer — never converts that failure into
    /// <see cref="PolicyReadStatus.NotConfigured"/>.
    /// </summary>
    Task<PolicyReadResult<AiAgentBehaviorPolicy>> GetEffectiveAsync(
        Guid tenantId,
        Guid? propertyId,
        CancellationToken cancellationToken = default);
}
