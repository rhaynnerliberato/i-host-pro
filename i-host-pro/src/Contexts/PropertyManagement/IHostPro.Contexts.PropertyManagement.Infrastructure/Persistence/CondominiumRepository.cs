using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>UserRepository</c> exactly — no explicit tenant filter needed, the DbContext's Global Query Filter already scopes every query.</remarks>
public sealed class CondominiumRepository : IRepository<Condominium, Guid>
{
    private readonly PropertyManagementDbContext _dbContext;

    public CondominiumRepository(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<Condominium?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Condominiums.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(Condominium aggregate) => _dbContext.Condominiums.Add(aggregate);

    public void Update(Condominium aggregate) => _dbContext.Condominiums.Update(aggregate);

    public void Remove(Condominium aggregate) => _dbContext.Condominiums.Remove(aggregate);
}
