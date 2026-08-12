using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>ReservationRepository</c> exactly — no explicit tenant filter needed, the HousekeepingDbContext's Global Query Filter already scopes every query.</remarks>
public sealed class CleaningRepository : IRepository<Cleaning, Guid>
{
    private readonly HousekeepingDbContext _dbContext;

    public CleaningRepository(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<Cleaning?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Cleanings.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(Cleaning aggregate) => _dbContext.Cleanings.Add(aggregate);

    public void Update(Cleaning aggregate) => _dbContext.Cleanings.Update(aggregate);

    public void Remove(Cleaning aggregate) => _dbContext.Cleanings.Remove(aggregate);
}
