using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <inheritdoc cref="ICleaningChecklistItemRepository"/>
public sealed class CleaningChecklistItemRepository : ICleaningChecklistItemRepository
{
    private readonly HousekeepingDbContext _dbContext;

    public CleaningChecklistItemRepository(HousekeepingDbContext dbContext) => _dbContext = dbContext;

    public async Task<CleaningChecklistItem?> GetAsync(
        Guid cleaningId, ChecklistItemType itemType, CancellationToken cancellationToken) =>
        await _dbContext.CleaningChecklistItems
            .FirstOrDefaultAsync(i => i.CleaningId == cleaningId && i.ItemType == itemType, cancellationToken);

    public void Add(CleaningChecklistItem item) => _dbContext.CleaningChecklistItems.Add(item);
}
