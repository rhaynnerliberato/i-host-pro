using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyAccessConfigurationRepository"/>
/// <remarks>Mirrors <c>FrontDeskContactRepository</c> exactly — no explicit tenant filter needed, the PropertyManagementDbContext's Global Query Filter already scopes every query.</remarks>
public sealed class PropertyAccessConfigurationRepository : IPropertyAccessConfigurationRepository
{
    private readonly PropertyManagementDbContext _dbContext;

    public PropertyAccessConfigurationRepository(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<PropertyAccessConfiguration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.PropertyAccessConfigurations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<PropertyAccessConfiguration?> GetByPropertyIdAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await _dbContext.PropertyAccessConfigurations.FirstOrDefaultAsync(c => c.PropertyId == propertyId, cancellationToken);

    public void Add(PropertyAccessConfiguration aggregate) => _dbContext.PropertyAccessConfigurations.Add(aggregate);

    public void Update(PropertyAccessConfiguration aggregate) => _dbContext.PropertyAccessConfigurations.Update(aggregate);

    public void Remove(PropertyAccessConfiguration aggregate) => _dbContext.PropertyAccessConfigurations.Remove(aggregate);
}
