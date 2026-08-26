using IHostPro.Contexts.Reservations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>reservations.reservations</c> — tenant-owned, RLS-protected (Fase 3,
/// Incremento 1 plan). <see cref="Reservation.PropertyId"/> carries NO
/// physical foreign key to <c>property_management.properties</c> (Incremento
/// 1 plan, item 4: "não criar FK física entre contextos") — same opaque-Guid
/// convention already used by <c>PropertyOwnerLink.OwnerUserId</c> across the
/// Identity/Property Management boundary; eligibility is validated via
/// <c>IPropertyReservationEligibilityReader</c> at write time, never enforced
/// by the database.
/// </summary>
public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.PropertyId).HasColumnName("property_id").IsRequired();

        builder.Property(r => r.GuestName).HasColumnName("guest_name").HasMaxLength(150).IsRequired();
        builder.Property(r => r.GuestPhone).HasColumnName("guest_phone").HasMaxLength(30);

        builder.Property(r => r.CheckInAt).HasColumnName("check_in_at").IsRequired();
        builder.Property(r => r.CheckOutAt).HasColumnName("check_out_at").IsRequired();
        builder.Property(r => r.GuestCount).HasColumnName("guest_count").IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        // Fase 9, Checkpoint 3.2 — "Airbnb Deterministic Foundation".
        builder.Property(r => r.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.ExternalReservationId).HasColumnName("external_reservation_id").HasMaxLength(200);

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors PropertyConfiguration/UserConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        // Incremento 1 plan, item 15 — the three minimum indexes.
        builder.HasIndex(r => new { r.TenantId, r.CheckInAt, r.Id });
        builder.HasIndex(r => new { r.TenantId, r.PropertyId, r.CheckInAt });
        builder.HasIndex(r => new { r.TenantId, r.Status, r.CheckInAt });

        // Fase 9, Checkpoint 3.2 — import idempotency: the same external
        // reservation, for the same tenant/source, must never be imported
        // twice. Partial (WHERE external_reservation_id IS NOT NULL) so
        // every Manual reservation (external_reservation_id always null)
        // never participates in this constraint at all.
        builder.HasIndex(r => new { r.TenantId, r.Source, r.ExternalReservationId })
            .IsUnique()
            .HasFilter("external_reservation_id IS NOT NULL");
    }
}
