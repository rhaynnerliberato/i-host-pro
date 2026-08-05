using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>PropertyRepository</c> exactly — no explicit tenant filter needed, the DbContext's Global Query Filter already scopes every query.</remarks>
public sealed class ReservationRepository : IRepository<Reservation, Guid>
{
    private readonly ReservationsDbContext _dbContext;

    public ReservationRepository(ReservationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Reservations.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(Reservation aggregate) => _dbContext.Reservations.Add(aggregate);

    public void Update(Reservation aggregate) => _dbContext.Reservations.Update(aggregate);

    public void Remove(Reservation aggregate) => _dbContext.Reservations.Remove(aggregate);
}
