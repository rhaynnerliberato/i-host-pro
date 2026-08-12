using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.cleaning_occurrences</c> — tenant-owned, RLS-protected,
/// append-only at the database privilege level (Fase 6, Incremento 2A) —
/// mirrors <c>CleaningAuditEntryConfiguration</c>. No physical foreign key
/// to <c>housekeeping.cleanings</c> (same opaque-reference convention this
/// context already uses for cross-context references — kept here too for
/// consistency, even though both tables live in the same schema).
/// </summary>
public sealed class CleaningOccurrenceConfiguration : IEntityTypeConfiguration<CleaningOccurrence>
{
    public void Configure(EntityTypeBuilder<CleaningOccurrence> builder)
    {
        builder.ToTable("cleaning_occurrences");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.CleaningId).HasColumnName("cleaning_id").IsRequired();
        builder.Property(e => e.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(e => e.RegisteredByUserId).HasColumnName("registered_by_user_id").IsRequired();
        builder.Property(e => e.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.CleaningId, e.RegisteredAtUtc });
    }
}
