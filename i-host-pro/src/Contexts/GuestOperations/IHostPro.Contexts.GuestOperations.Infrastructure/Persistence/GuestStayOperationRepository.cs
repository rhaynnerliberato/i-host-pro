using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>ReservationRepository</c> exactly — no explicit tenant filter needed, the GuestOperationsDbContext's Global Query Filter already scopes every query.</remarks>
public sealed class GuestStayOperationRepository : IRepository<GuestStayOperation, Guid>
{
    private readonly GuestOperationsDbContext _dbContext;

    public GuestStayOperationRepository(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<GuestStayOperation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.GuestStayOperations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public void Add(GuestStayOperation aggregate) => _dbContext.GuestStayOperations.Add(aggregate);

    public void Update(GuestStayOperation aggregate) => _dbContext.GuestStayOperations.Update(aggregate);

    public void Remove(GuestStayOperation aggregate) => _dbContext.GuestStayOperations.Remove(aggregate);
}
