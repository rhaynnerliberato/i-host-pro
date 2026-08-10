using IHostPro.Contexts.Configuration.Application.Policies;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <inheritdoc cref="IPolicyDefinitionReader"/>
public sealed class PolicyDefinitionReader : IPolicyDefinitionReader
{
    private readonly ConfigurationDbContext _dbContext;

    public PolicyDefinitionReader(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<PolicyDefinitionResult>> ListAsync(CancellationToken cancellationToken)
    {
        var definitions = await _dbContext.PolicyDefinitions
            .AsNoTracking()
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

        return definitions
            .Select(d => new PolicyDefinitionResult(
                d.Id, d.Name, d.Description, d.Category, d.ValueType.ToString(), d.SchemaVersion, d.IsActive))
            .ToList();
    }

    public async Task<bool> ExistsAsync(string policyCode, CancellationToken cancellationToken) =>
        await _dbContext.PolicyDefinitions
            .AsNoTracking()
            .AnyAsync(d => d.Id == policyCode && d.IsActive, cancellationToken);
}
