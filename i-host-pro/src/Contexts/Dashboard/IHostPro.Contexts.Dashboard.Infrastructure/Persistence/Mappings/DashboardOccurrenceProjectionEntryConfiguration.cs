using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>dashboard.occurrence_projection</c> — tenant-owned, RLS-protected
/// local read-model, built exclusively from Housekeeping's own
/// <c>CleaningOccurrenceRegistered</c> Integration Event (Fase 7, Incremento
/// 2 — Dashboard &amp; Reporting Foundation, Checkpoint 1). Append-only.
/// </summary>
public sealed class DashboardOccurrenceProjectionEntryConfiguration : IEntityTypeConfiguration<DashboardOccurrenceProjectionEntry>
{
    public void Configure(EntityTypeBuilder<DashboardOccurrenceProjectionEntry> builder)
    {
        builder.ToTable("occurrence_projection");

        builder.HasKey(o => new { o.TenantId, o.OccurrenceId });

        builder.Property(o => o.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(o => o.OccurrenceId).HasColumnName("occurrence_id").IsRequired();
        builder.Property(o => o.CleaningId).HasColumnName("cleaning_id").IsRequired();
        builder.Property(o => o.Type).HasColumnName("type").HasMaxLength(30).IsRequired();
        builder.Property(o => o.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();

        // MVP indicators (Checkpoint 0 decision, §8): total in an interval,
        // distribution by type.
        builder.HasIndex(o => new { o.TenantId, o.RegisteredAtUtc });
        builder.HasIndex(o => new { o.TenantId, o.Type });
        builder.HasIndex(o => new { o.TenantId, o.CleaningId });
    }
}
