using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Read-only, untracked access to <c>PolicyValue</c> rows for this context's
/// own administrative API (exact-scope lookup and version history) —
/// implemented in Infrastructure. Never used for hierarchical resolution
/// (that is <see cref="IHostPro.Contexts.Configuration.Contracts.IEarlyCheckInPolicyReader"/>/
/// <see cref="IHostPro.Contexts.Configuration.Contracts.ILateCheckoutPolicyReader"/>'s job).
/// </summary>
public interface IPolicyValueReader
{
    Task<PolicyValueDetailResult?> GetCurrentAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PolicyValueDetailResult>> GetHistoryAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken);
}
