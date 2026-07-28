using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="ISecurityAuditWriter"/>
public sealed class SecurityAuditWriter : ISecurityAuditWriter
{
    private readonly IdentityDbContext _dbContext;

    public SecurityAuditWriter(IdentityDbContext dbContext) => _dbContext = dbContext;

    public void Record(SecurityAuditEntry entry) => _dbContext.SecurityAuditLog.Add(entry);
}
