using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>CondominiumRepository</c> exactly — no explicit tenant filter needed, the PropertyManagementDbContext's Global Query Filter already scopes every query.</remarks>
public sealed class PropertyRepository : IRepository<Property, Guid>
{
    private readonly PropertyManagementDbContext _dbContext;

    public PropertyRepository(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<Property?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Properties.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public void Add(Property aggregate) => _dbContext.Properties.Add(aggregate);

    public void Update(Property aggregate) => _dbContext.Properties.Update(aggregate);

    public void Remove(Property aggregate) => _dbContext.Properties.Remove(aggregate);
}
