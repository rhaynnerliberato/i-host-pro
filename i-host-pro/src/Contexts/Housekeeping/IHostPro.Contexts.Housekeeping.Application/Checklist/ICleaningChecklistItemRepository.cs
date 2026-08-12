using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>
/// Composite-key (<c>CleaningId</c>+<c>ItemType</c>) upsert lookup — not the
/// shared generic <c>IRepository&lt;TAggregate,TId&gt;</c> (that interface's
/// single-Guid <c>GetByIdAsync</c> does not fit a composite key).
/// </summary>
public interface ICleaningChecklistItemRepository
{
    Task<CleaningChecklistItem?> GetAsync(
        Guid cleaningId, ChecklistItemType itemType, CancellationToken cancellationToken);

    void Add(CleaningChecklistItem item);
}
