using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Write access to <c>PolicyValue</c> for creating a new version — not the
/// generic <c>IRepository&lt;TAggregate,TId&gt;</c> shape (its
/// <c>GetByIdAsync</c> looks up by primary key, but a new version's write
/// path needs the CURRENT row for a given scope, looked up by business key,
/// tracked so <see cref="PolicyValue.Supersede"/> is picked up by
/// <c>SaveChangesAsync</c>).
/// </summary>
public interface IPolicyValueRepository
{
    Task<PolicyValue?> GetCurrentTrackedAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken);

    void Add(PolicyValue policyValue);
}
