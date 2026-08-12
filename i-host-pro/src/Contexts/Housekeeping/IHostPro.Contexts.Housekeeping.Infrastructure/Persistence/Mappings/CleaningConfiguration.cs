using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.cleanings</c> — tenant-owned, RLS-protected (Fase 6,
/// Incremento 1). <see cref="Cleaning.PropertyId"/>/<see cref="Cleaning.ReservationId"/>
/// carry NO physical foreign key to <c>property_management.properties</c>/
/// <c>reservations.reservations</c> (same opaque-Guid convention as
/// <c>Reservation.PropertyId</c>) — validated at write time via this
/// context's own local projections, never enforced by the database.
/// </summary>
public sealed class CleaningConfiguration : IEntityTypeConfiguration<Cleaning>
{
    public void Configure(EntityTypeBuilder<Cleaning> builder)
    {
        builder.ToTable("cleanings");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(c => c.ReservationId).HasColumnName("reservation_id");
        builder.Property(c => c.AssignedHousekeeperUserId).HasColumnName("assigned_housekeeper_user_id");

        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(c => c.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(c => c.InspectionStartedAtUtc).HasColumnName("inspection_started_at_utc");
        builder.Property(c => c.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(c => c.CancelledAtUtc).HasColumnName("cancelled_at_utc");

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in
        // system column `xmin` — mirrors ReservationConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.Status, c.CreatedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.PropertyId, c.CreatedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.AssignedHousekeeperUserId });

        // Non-unique — a Reservation may, in principle, be linked to more
        // than one Cleaning across its lifetime; the lookup used to react to
        // ReservationCancelled (Checkpoint 3) filters by status too.
        builder.HasIndex(c => new { c.TenantId, c.ReservationId });
    }
}
