using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <inheritdoc cref="IPolicyAuditWriter"/>
public sealed class PolicyAuditWriter : IPolicyAuditWriter
{
    private readonly ConfigurationDbContext _dbContext;

    public PolicyAuditWriter(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public void Record(PolicyAuditEntry entry) => _dbContext.PolicyAuditLog.Add(entry);
}
