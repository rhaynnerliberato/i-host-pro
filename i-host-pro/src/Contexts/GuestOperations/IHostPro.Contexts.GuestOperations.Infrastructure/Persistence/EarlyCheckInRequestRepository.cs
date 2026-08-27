using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>GuestStayOperationRepository</c> exactly.</remarks>
public sealed class EarlyCheckInRequestRepository : IRepository<EarlyCheckInRequest, Guid>
{
    private readonly GuestOperationsDbContext _dbContext;

    public EarlyCheckInRequestRepository(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<EarlyCheckInRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.EarlyCheckInRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(EarlyCheckInRequest aggregate) => _dbContext.EarlyCheckInRequests.Add(aggregate);

    public void Update(EarlyCheckInRequest aggregate) => _dbContext.EarlyCheckInRequests.Update(aggregate);

    public void Remove(EarlyCheckInRequest aggregate) => _dbContext.EarlyCheckInRequests.Remove(aggregate);
}
