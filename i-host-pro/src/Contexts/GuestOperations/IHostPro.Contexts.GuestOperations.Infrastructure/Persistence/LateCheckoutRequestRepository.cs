using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors <c>GuestStayOperationRepository</c> exactly.</remarks>
public sealed class LateCheckoutRequestRepository : IRepository<LateCheckoutRequest, Guid>
{
    private readonly GuestOperationsDbContext _dbContext;

    public LateCheckoutRequestRepository(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<LateCheckoutRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.LateCheckoutRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public void Add(LateCheckoutRequest aggregate) => _dbContext.LateCheckoutRequests.Add(aggregate);

    public void Update(LateCheckoutRequest aggregate) => _dbContext.LateCheckoutRequests.Update(aggregate);

    public void Remove(LateCheckoutRequest aggregate) => _dbContext.LateCheckoutRequests.Remove(aggregate);
}
