using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <inheritdoc cref="IPolicyValueReader"/>
public sealed class PolicyValueReader : IPolicyValueReader
{
    private readonly ConfigurationDbContext _dbContext;

    public PolicyValueReader(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public async Task<PolicyValueDetailResult?> GetCurrentAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken)
    {
        var value = await _dbContext.PolicyValues
            .AsNoTracking()
            .Where(v =>
                v.TenantId == tenantId && v.PolicyCode == policyCode &&
                v.ScopeType == scopeType && v.ScopeReferenceId == scopeReferenceId && v.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken);

        return value is null ? null : ToResult(value);
    }

    public async Task<IReadOnlyList<PolicyValueDetailResult>> GetHistoryAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken)
    {
        var values = await _dbContext.PolicyValues
            .AsNoTracking()
            .Where(v =>
                v.TenantId == tenantId && v.PolicyCode == policyCode &&
                v.ScopeType == scopeType && v.ScopeReferenceId == scopeReferenceId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(cancellationToken);

        return values.Select(ToResult).ToList();
    }

    private static PolicyValueDetailResult ToResult(PolicyValue value) => new(
        value.Id, value.PolicyCode, value.ScopeType.ToString(), value.ScopeReferenceId, value.Version,
        value.Value, value.CreatedAtUtc, value.CreatedByUserId, value.Reason, value.IsCurrent);
}
