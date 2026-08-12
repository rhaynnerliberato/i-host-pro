using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.cleaning_checklist_items</c> — tenant-owned,
/// RLS-protected (Fase 6, Incremento 2A). Unlike <c>cleaning_occurrences</c>/
/// <c>cleaning_audit_log</c>, this table IS mutable (a checkbox toggle), so
/// no UPDATE privilege restriction is applied. A unique index on
/// <c>(tenant_id, cleaning_id, item_type)</c> enforces at most one row per
/// item per cleaning — the upsert-by-composite-key <c>CleaningChecklistItemRepository</c>
/// relies on.
/// </summary>
public sealed class CleaningChecklistItemConfiguration : IEntityTypeConfiguration<CleaningChecklistItem>
{
    public void Configure(EntityTypeBuilder<CleaningChecklistItem> builder)
    {
        builder.ToTable("cleaning_checklist_items");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.CleaningId).HasColumnName("cleaning_id").IsRequired();
        builder.Property(e => e.ItemType).HasColumnName("item_type").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(e => e.IsChecked).HasColumnName("is_checked").IsRequired();
        builder.Property(e => e.UpdatedByUserId).HasColumnName("updated_by_user_id").IsRequired();
        builder.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.CleaningId, e.ItemType }).IsUnique();
    }
}
