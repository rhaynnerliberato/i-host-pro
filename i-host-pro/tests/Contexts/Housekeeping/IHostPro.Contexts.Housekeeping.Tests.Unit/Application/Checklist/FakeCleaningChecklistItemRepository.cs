using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Checklist;

internal sealed class FakeCleaningChecklistItemRepository : ICleaningChecklistItemRepository
{
    private readonly CleaningChecklistItem? _existingItem;

    private FakeCleaningChecklistItemRepository(CleaningChecklistItem? existingItem) => _existingItem = existingItem;

    public static FakeCleaningChecklistItemRepository WithExistingItem(CleaningChecklistItem? existingItem) => new(existingItem);

    public List<CleaningChecklistItem> AddedItems { get; } = [];

    public Task<CleaningChecklistItem?> GetAsync(Guid cleaningId, ChecklistItemType itemType, CancellationToken cancellationToken) =>
        Task.FromResult(_existingItem is not null && _existingItem.CleaningId == cleaningId && _existingItem.ItemType == itemType
            ? _existingItem
            : null);

    public void Add(CleaningChecklistItem item) => AddedItems.Add(item);
}
