using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningOccurrenceReader"/>
/// <remarks>
/// Joins against <see cref="HousekeepingDbContext.Cleanings"/> to enforce the
/// ABAC ownership check directly in the query — defense in depth alongside
/// <c>ListCleaningOccurrencesQueryHandler</c>'s own upfront ownership check.
/// </remarks>
public sealed class CleaningOccurrenceReader : ICleaningOccurrenceReader
{
    private readonly HousekeepingDbContext _dbContext;

    public CleaningOccurrenceReader(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<CleaningOccurrenceResult>> ListForOwnCleaningAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken)
    {
        var occurrences = await (
            from occurrence in _dbContext.CleaningOccurrences.AsNoTracking()
            join cleaning in _dbContext.Cleanings.AsNoTracking() on occurrence.CleaningId equals cleaning.Id
            where occurrence.CleaningId == cleaningId && cleaning.AssignedHousekeeperUserId == housekeeperUserId
            orderby occurrence.RegisteredAtUtc
            select occurrence)
            .ToListAsync(cancellationToken);

        return occurrences.Select(ToResult).ToArray();
    }

    private static CleaningOccurrenceResult ToResult(CleaningOccurrence occurrence) => new(
        occurrence.Id,
        occurrence.CleaningId,
        OccurrenceTypeCodeMapper.ToCode(occurrence.Type),
        occurrence.Description,
        occurrence.RegisteredByUserId,
        occurrence.RegisteredAtUtc);
}
