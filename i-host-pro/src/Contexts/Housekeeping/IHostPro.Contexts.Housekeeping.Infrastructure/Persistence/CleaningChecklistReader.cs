using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningChecklistReader"/>
public sealed class CleaningChecklistReader : ICleaningChecklistReader
{
    private readonly HousekeepingDbContext _dbContext;

    public CleaningChecklistReader(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<CleaningChecklistItemResult>> GetForCleaningAsync(
        Guid cleaningId, CancellationToken cancellationToken)
    {
        var persisted = await _dbContext.CleaningChecklistItems
            .AsNoTracking()
            .Where(i => i.CleaningId == cleaningId)
            .ToDictionaryAsync(i => i.ItemType, cancellationToken);

        return Enum.GetValues<ChecklistItemType>()
            .Select(itemType => persisted.TryGetValue(itemType, out var item)
                ? new CleaningChecklistItemResult(
                    cleaningId, ChecklistItemTypeCodeMapper.ToCode(itemType), item.IsChecked, item.UpdatedByUserId, item.UpdatedAtUtc)
                : new CleaningChecklistItemResult(cleaningId, ChecklistItemTypeCodeMapper.ToCode(itemType), false, null, null))
            .ToArray();
    }
}
