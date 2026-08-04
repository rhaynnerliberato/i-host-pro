using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyAuditWriter"/>
public sealed class PropertyAuditWriter : IPropertyAuditWriter
{
    private readonly PropertyManagementDbContext _dbContext;

    public PropertyAuditWriter(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public void Record(PropertyAuditEntry entry) => _dbContext.PropertyAuditLog.Add(entry);
}
