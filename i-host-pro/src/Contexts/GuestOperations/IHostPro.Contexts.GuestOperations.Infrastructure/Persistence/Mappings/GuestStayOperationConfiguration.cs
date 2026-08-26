using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>guest_operations.guest_stay_operations</c> — tenant-owned,
/// RLS-protected (Fase 10, Checkpoint 1). <see cref="GuestStayOperation.ReservationId"/>/
/// <see cref="GuestStayOperation.PropertyId"/> carry NO physical foreign key
/// to <c>reservations.reservations</c>/<c>property_management.properties</c>
/// (mirrors <c>ReservationConfiguration</c>'s own opaque-Guid convention) —
/// eligibility/existence is the sending command's own responsibility, never
/// enforced by this database.
/// </summary>
public sealed class GuestStayOperationConfiguration : IEntityTypeConfiguration<GuestStayOperation>
{
    public void Configure(EntityTypeBuilder<GuestStayOperation> builder)
    {
        builder.ToTable("guest_stay_operations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(o => new { o.TenantId, o.Id });

        builder.Property(o => o.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(o => o.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(o => o.PropertyId).HasColumnName("property_id").IsRequired();

        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(o => o.CheckedInAtUtc).HasColumnName("checked_in_at_utc");
        builder.Property(o => o.CheckedOutAtUtc).HasColumnName("checked_out_at_utc");

        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(o => o.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors ReservationConfiguration/PropertyConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        // Exactly one active GuestStayOperation per Reservation (CP1 mandate).
        builder.HasIndex(o => new { o.TenantId, o.ReservationId }).IsUnique();
    }
}
