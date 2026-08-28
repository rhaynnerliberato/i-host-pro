using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence;

/// <inheritdoc cref="IPixChargeReader"/>
public sealed class PixChargeReader : IPixChargeReader
{
    private readonly PaymentsDbContext _dbContext;

    public PixChargeReader(PaymentsDbContext dbContext) => _dbContext = dbContext;

    public async Task<Guid?> GetActiveIdByLateCheckoutRequestIdAsync(Guid lateCheckoutRequestId, CancellationToken cancellationToken) =>
        await _dbContext.PixCharges
            .AsNoTracking()
            .Where(c => c.LateCheckoutRequestId == lateCheckoutRequestId && c.Status == PixChargeStatus.Pending)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
