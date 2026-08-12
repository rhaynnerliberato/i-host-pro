using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.reservation_projection</c> — tenant-owned, RLS-protected
/// local read-model, built exclusively from Reservations' own
/// <c>ReservationCreated</c> (Fase 6, Incremento 1, Checkpoint 0/3 gate).
/// </summary>
public sealed class ReservationProjectionEntryConfiguration : IEntityTypeConfiguration<ReservationProjectionEntry>
{
    public void Configure(EntityTypeBuilder<ReservationProjectionEntry> builder)
    {
        builder.ToTable("reservation_projection");

        builder.HasKey(r => new { r.TenantId, r.ReservationId });

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.ReservationId).HasColumnName("reservation_id").IsRequired();
    }
}
