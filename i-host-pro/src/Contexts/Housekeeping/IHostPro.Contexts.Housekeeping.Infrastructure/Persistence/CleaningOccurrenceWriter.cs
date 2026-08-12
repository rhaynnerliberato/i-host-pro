using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningOccurrenceWriter"/>
public sealed class CleaningOccurrenceWriter : ICleaningOccurrenceWriter
{
    private readonly HousekeepingDbContext _dbContext;

    public CleaningOccurrenceWriter(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public void Record(CleaningOccurrence occurrence) => _dbContext.CleaningOccurrences.Add(occurrence);
}
