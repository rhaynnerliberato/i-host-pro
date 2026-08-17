using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>dashboard.cleaning_projection</c> — tenant-owned, RLS-protected local
/// read-model, built exclusively from Housekeeping's own Cleaning lifecycle
/// Integration Events (Fase 7, Incremento 2 — Dashboard &amp; Reporting
/// Foundation, Checkpoint 1).
/// </summary>
public sealed class DashboardCleaningProjectionEntryConfiguration : IEntityTypeConfiguration<DashboardCleaningProjectionEntry>
{
    public void Configure(EntityTypeBuilder<DashboardCleaningProjectionEntry> builder)
    {
        builder.ToTable("cleaning_projection");

        builder.HasKey(c => new { c.TenantId, c.CleaningId });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.CleaningId).HasColumnName("cleaning_id").IsRequired();
        builder.Property(c => c.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(c => c.HousekeeperUserId).HasColumnName("housekeeper_user_id");
        builder.Property(c => c.ScheduledAtUtc).HasColumnName("scheduled_at_utc");
        builder.Property(c => c.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(c => c.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(c => c.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(c => c.CancelledAtUtc).HasColumnName("cancelled_at_utc");
        builder.Property(c => c.LastEventAtUtc).HasColumnName("last_event_at_utc").IsRequired();

        // MVP indicators (Checkpoint 0 decision, §23; Checkpoint 2, §15/§20/
        // §21): current-state counts by status, atrasadas (ScheduledAtUtc <
        // now AND status not terminal), by property, completed/cancelled
        // within an interval.
        builder.HasIndex(c => new { c.TenantId, c.Status });
        builder.HasIndex(c => new { c.TenantId, c.ScheduledAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.PropertyId });
        builder.HasIndex(c => new { c.TenantId, c.CompletedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.CancelledAtUtc });
    }
}
