using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>dashboard.property_projection</c> — tenant-owned, RLS-protected local
/// read-model, built exclusively from Property Management's own
/// <c>PropertyCreated</c>/<c>PropertyActivated</c>/<c>PropertyDeactivated</c>/
/// <c>PropertyArchived</c> Integration Events (Fase 7, Incremento 2 —
/// Dashboard &amp; Reporting Foundation, Checkpoint 1). Deliberately a
/// separate table/name from Housekeeping's own <c>property_projection</c>
/// (different schema, different owning Bounded Context, different purpose —
/// no relation).
/// </summary>
public sealed class DashboardPropertyProjectionEntryConfiguration : IEntityTypeConfiguration<DashboardPropertyProjectionEntry>
{
    public void Configure(EntityTypeBuilder<DashboardPropertyProjectionEntry> builder)
    {
        builder.ToTable("property_projection");

        builder.HasKey(p => new { p.TenantId, p.PropertyId });

        builder.Property(p => p.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(p => p.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(p => p.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(p => p.LastEventAtUtc).HasColumnName("last_event_at_utc").IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.Status });
    }
}
