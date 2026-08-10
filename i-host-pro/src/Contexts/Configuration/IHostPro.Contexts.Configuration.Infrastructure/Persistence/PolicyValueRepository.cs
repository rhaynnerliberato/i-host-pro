using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <inheritdoc cref="IPolicyValueRepository"/>
public sealed class PolicyValueRepository : IPolicyValueRepository
{
    private readonly ConfigurationDbContext _dbContext;

    public PolicyValueRepository(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public Task<PolicyValue?> GetCurrentTrackedAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken) =>
        _dbContext.PolicyValues
            .Where(v =>
                v.TenantId == tenantId && v.PolicyCode == policyCode &&
                v.ScopeType == scopeType && v.ScopeReferenceId == scopeReferenceId && v.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(PolicyValue policyValue) => _dbContext.PolicyValues.Add(policyValue);
}
