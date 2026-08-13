using IHostPro.Contexts.Reservations.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>reservations.cleaning_schedule_projection</c> — tenant-owned,
/// RLS-protected local read-model, built exclusively from Housekeeping's own
/// Cleaning lifecycle Integration Events (Fase 7, Incremento 1 — Agenda
/// Foundation).
/// </summary>
public sealed class CleaningScheduleProjectionEntryConfiguration : IEntityTypeConfiguration<CleaningScheduleProjectionEntry>
{
    public void Configure(EntityTypeBuilder<CleaningScheduleProjectionEntry> builder)
    {
        builder.ToTable("cleaning_schedule_projection");

        builder.HasKey(c => new { c.TenantId, c.CleaningId });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.CleaningId).HasColumnName("cleaning_id").IsRequired();
        builder.Property(c => c.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(c => c.AssignedHousekeeperUserId).HasColumnName("assigned_housekeeper_user_id");
        builder.Property(c => c.ScheduledAtUtc).HasColumnName("scheduled_at_utc");
        builder.Property(c => c.Status).HasColumnName("status").HasMaxLength(20).IsRequired();

        // Agenda's own read query (GET /api/v1/schedule) filters by tenant +
        // property/housekeeper + a ScheduledAtUtc window — the same shape as
        // Reservations' own three indexes on the reservations table.
        builder.HasIndex(c => new { c.TenantId, c.ScheduledAtUtc, c.CleaningId });
        builder.HasIndex(c => new { c.TenantId, c.PropertyId, c.ScheduledAtUtc });
    }
}
