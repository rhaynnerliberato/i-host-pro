namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The single, typed synchronous query port another Bounded Context may use
/// to resolve the effective <c>LATE_CHECKOUT</c> policy — Fase 5, Incremento
/// 1 official decisions §5: no generic string-based reader is ever exposed
/// here, only this policy-specific contract. Implemented ONLY in
/// <c>Configuration.Infrastructure</c> — a consumer may reference this
/// contract, never <c>Configuration.Application</c>/<c>Infrastructure</c>/
/// <c>Api</c>, and never <c>ConfigurationDbContext</c>/the <c>configuration</c>
/// schema directly.
/// </summary>
public interface ILateCheckoutPolicyReader
{
    /// <summary>
    /// Resolves the effective value following PROPERTY → TENANT → GLOBAL
    /// precedence. When <paramref name="propertyId"/> is <c>null</c>, only
    /// TENANT → GLOBAL are considered. Throws
    /// <see cref="PolicyEngineUnavailableException"/> when the engine cannot
    /// answer — never converts that failure into
    /// <see cref="PolicyReadStatus.NotConfigured"/>.
    /// </summary>
    Task<PolicyReadResult<LateCheckoutPolicy>> GetEffectiveAsync(
        Guid tenantId,
        Guid? propertyId,
        CancellationToken cancellationToken = default);
}
