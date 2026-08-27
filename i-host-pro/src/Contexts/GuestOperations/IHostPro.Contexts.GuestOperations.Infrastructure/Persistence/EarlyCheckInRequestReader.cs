using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IEarlyCheckInRequestReader"/>
public sealed class EarlyCheckInRequestReader : IEarlyCheckInRequestReader
{
    private readonly GuestOperationsDbContext _dbContext;

    public EarlyCheckInRequestReader(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.EarlyCheckInRequests
            .AsNoTracking()
            .AnyAsync(r => r.ReservationId == reservationId && r.Status == EarlyCheckInRequestStatus.Pending, cancellationToken);
}
