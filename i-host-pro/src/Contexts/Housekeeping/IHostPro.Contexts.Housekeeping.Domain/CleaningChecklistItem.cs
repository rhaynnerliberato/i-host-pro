using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Domain;

/// <summary>
/// The checked/unchecked state of a single fixed checklist item for one
/// Cleaning (Fase 6, Incremento 2A) — Documento 12's "Checklist": "Representa
/// itens de inspeção." A row is created lazily, only when the housekeeper
/// first toggles that item (never eagerly seeded for all 8 items at Cleaning
/// creation) — an item with no row is simply unchecked by default, never an
/// invented/persisted default. Unlike <see cref="CleaningOccurrence"/>, this
/// is mutable in place (a checkbox, not an append-only fact). Never gates
/// <see cref="Cleaning.Complete"/> (approval §17 — no documented rule makes
/// it a completion requirement).
/// </summary>
public sealed class CleaningChecklistItem : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid CleaningId { get; private set; }
    public ChecklistItemType ItemType { get; private set; }
    public bool IsChecked { get; private set; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private CleaningChecklistItem()
    {
        // EF Core materialization.
    }

    private CleaningChecklistItem(
        Guid id, Guid tenantId, Guid cleaningId, ChecklistItemType itemType, bool isChecked,
        Guid updatedByUserId, DateTimeOffset updatedAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        CleaningId = cleaningId;
        ItemType = itemType;
        IsChecked = isChecked;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    public static CleaningChecklistItem Create(
        Guid id, Guid tenantId, Guid cleaningId, ChecklistItemType itemType, bool isChecked,
        Guid updatedByUserId, DateTimeOffset updatedAtUtc) =>
        new(id, tenantId, cleaningId, itemType, isChecked, updatedByUserId, updatedAtUtc.ToUniversalTime());

    public void SetChecked(bool isChecked, Guid updatedByUserId, DateTimeOffset now)
    {
        IsChecked = isChecked;
        UpdatedByUserId = updatedByUserId;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
