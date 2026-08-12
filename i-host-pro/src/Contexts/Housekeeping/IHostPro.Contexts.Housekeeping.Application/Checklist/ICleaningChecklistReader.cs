namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>
/// Always returns all 8 fixed catalog items (Fase 6, Incremento 2A) — items
/// with no persisted row are materialized as unchecked, never omitted.
/// </summary>
public interface ICleaningChecklistReader
{
    Task<IReadOnlyList<CleaningChecklistItemResult>> GetForCleaningAsync(Guid cleaningId, CancellationToken cancellationToken);
}
