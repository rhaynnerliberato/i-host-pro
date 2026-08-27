using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="ILateCheckoutRequestReader"/>
public sealed class LateCheckoutRequestReader : ILateCheckoutRequestReader
{
    private readonly GuestOperationsDbContext _dbContext;

    public LateCheckoutRequestReader(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.LateCheckoutRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.ReservationId == reservationId
                    && (r.Status == LateCheckoutRequestStatus.Pending || r.Status == LateCheckoutRequestStatus.PendingPayment),
                cancellationToken);
}
