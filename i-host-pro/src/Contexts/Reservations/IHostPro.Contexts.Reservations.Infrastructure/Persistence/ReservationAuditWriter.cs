using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <inheritdoc cref="IReservationAuditWriter"/>
public sealed class ReservationAuditWriter : IReservationAuditWriter
{
    private readonly ReservationsDbContext _dbContext;

    public ReservationAuditWriter(ReservationsDbContext dbContext) => _dbContext = dbContext;

    public void Record(ReservationAuditEntry entry) => _dbContext.ReservationAuditLog.Add(entry);
}
