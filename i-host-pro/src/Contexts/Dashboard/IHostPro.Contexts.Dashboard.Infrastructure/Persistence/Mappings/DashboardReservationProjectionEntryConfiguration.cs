using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>dashboard.reservation_projection</c> — tenant-owned, RLS-protected
/// local read-model, built exclusively from Reservations' own
/// <c>ReservationCreated</c>/<c>ReservationUpdated</c>/<c>ReservationCancelled</c>
/// Integration Events (Fase 7, Incremento 2 — Dashboard &amp; Reporting
/// Foundation, Checkpoint 1).
/// </summary>
public sealed class DashboardReservationProjectionEntryConfiguration : IEntityTypeConfiguration<DashboardReservationProjectionEntry>
{
    public void Configure(EntityTypeBuilder<DashboardReservationProjectionEntry> builder)
    {
        builder.ToTable("reservation_projection");

        builder.HasKey(r => new { r.TenantId, r.ReservationId });

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(r => r.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(r => r.CheckInAt).HasColumnName("check_in_at");
        builder.Property(r => r.CheckOutAt).HasColumnName("check_out_at");
        builder.Property(r => r.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(r => r.CancelledAtUtc).HasColumnName("cancelled_at_utc");
        builder.Property(r => r.LastEventAtUtc).HasColumnName("last_event_at_utc").IsRequired();

        // MVP indicators (Checkpoint 0 decision, §5/§23; Checkpoint 2, §13):
        // check-ins/check-outs/cancellations within an interval, by tenant.
        builder.HasIndex(r => new { r.TenantId, r.CheckInAt });
        builder.HasIndex(r => new { r.TenantId, r.CheckOutAt });
        builder.HasIndex(r => new { r.TenantId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.CancelledAtUtc });
    }
}
