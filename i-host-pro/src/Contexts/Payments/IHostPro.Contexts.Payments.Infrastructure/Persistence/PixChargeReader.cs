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

    /// <inheritdoc cref="IPixChargeReader.GetStatusByReservationIdAsync"/>
    public async Task<PaymentStatusResult?> GetStatusByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var charge = await _dbContext.PixCharges
            .AsNoTracking()
            .Where(c => c.ReservationId == reservationId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return charge is null
            ? null
            : new PaymentStatusResult(charge.Status.ToString(), charge.Amount, charge.CurrencyCode, charge.ExpiresAtUtc);
    }
}
