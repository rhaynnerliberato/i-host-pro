using IHostPro.Contexts.GuestOperations.Application;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;

/// <inheritdoc cref="IGuestStayOperationReader"/>
public sealed class GuestStayOperationReader : IGuestStayOperationReader
{
    private readonly GuestOperationsDbContext _dbContext;

    public GuestStayOperationReader(GuestOperationsDbContext dbContext) => _dbContext = dbContext;

    public async Task<Guid?> GetIdByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
        await _dbContext.GuestStayOperations
            .AsNoTracking()
            .Where(o => o.ReservationId == reservationId)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
}
