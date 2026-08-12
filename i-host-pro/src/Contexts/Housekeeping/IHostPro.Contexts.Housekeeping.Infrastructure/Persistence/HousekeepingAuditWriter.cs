using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="IHousekeepingAuditWriter"/>
public sealed class HousekeepingAuditWriter : IHousekeepingAuditWriter
{
    private readonly HousekeepingDbContext _dbContext;

    public HousekeepingAuditWriter(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public void Record(CleaningAuditEntry entry) => _dbContext.CleaningAuditLog.Add(entry);
}
