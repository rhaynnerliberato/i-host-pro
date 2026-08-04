using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyOwnerWriter"/>
public sealed class PropertyOwnerWriter : IPropertyOwnerWriter
{
    private readonly PropertyManagementDbContext _dbContext;

    public PropertyOwnerWriter(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public void Link(PropertyOwnerLink link) => _dbContext.PropertyOwnerLinks.Add(link);

    public void Unlink(PropertyOwnerLink link) => _dbContext.PropertyOwnerLinks.Remove(link);
}
