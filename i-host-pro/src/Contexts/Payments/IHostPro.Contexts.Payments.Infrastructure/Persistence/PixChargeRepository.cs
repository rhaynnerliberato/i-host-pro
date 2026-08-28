using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>Mirrors every other Bounded Context's own aggregate repository exactly.</remarks>
public sealed class PixChargeRepository : IRepository<PixCharge, Guid>
{
    private readonly PaymentsDbContext _dbContext;

    public PixChargeRepository(PaymentsDbContext dbContext) => _dbContext = dbContext;

    public async Task<PixCharge?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.PixCharges.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(PixCharge aggregate) => _dbContext.PixCharges.Add(aggregate);

    public void Update(PixCharge aggregate) => _dbContext.PixCharges.Update(aggregate);

    public void Remove(PixCharge aggregate) => _dbContext.PixCharges.Remove(aggregate);
}
