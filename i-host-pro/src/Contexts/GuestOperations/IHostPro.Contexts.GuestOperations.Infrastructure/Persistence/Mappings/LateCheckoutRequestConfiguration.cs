using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>guest_operations.late_checkout_requests</c> — tenant-owned,
/// RLS-protected (Fase 10, Checkpoint 3). <see cref="LateCheckoutRequest.ReservationId"/>/
/// <see cref="LateCheckoutRequest.PropertyId"/> carry no physical foreign key
/// — mirrors <c>GuestStayOperationConfiguration</c>'s own opaque-Guid
/// convention. At most one active (<c>Pending</c> OR <c>PendingPayment</c>)
/// request may exist per Reservation at a time (mandate cardinality rule) —
/// enforced by a partial unique index, never at the Domain layer.
/// </summary>
public sealed class LateCheckoutRequestConfiguration : IEntityTypeConfiguration<LateCheckoutRequest>
{
    public void Configure(EntityTypeBuilder<LateCheckoutRequest> builder)
    {
        builder.ToTable("late_checkout_requests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(r => r.PropertyId).HasColumnName("property_id").IsRequired();

        builder.Property(r => r.RequestedCheckOutAt).HasColumnName("requested_check_out_at").IsRequired();

        builder.Property(r => r.ChargeType).HasColumnName("charge_type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.ChargeValue).HasColumnName("charge_value").HasColumnType("numeric(12,2)");
        builder.Property(r => r.RequiresPix).HasColumnName("requires_pix").IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.DenialReason).HasColumnName("denial_reason").HasConversion<string>().HasMaxLength(30);

        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(r => r.DecidedAtUtc).HasColumnName("decided_at_utc");
        builder.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors GuestStayOperationConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        // Cardinality rule: at most one Pending OR PendingPayment request per
        // Reservation — PendingPayment counts as active (mandate decision).
        builder.HasIndex(r => new { r.TenantId, r.ReservationId }, "ix_late_checkout_requests_tenant_id_reservation_id_active_unique")
            .IsUnique()
            .HasFilter("status IN ('Pending', 'PendingPayment')");
    }
}
