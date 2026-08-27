using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>guest_operations.early_check_in_requests</c> — tenant-owned,
/// RLS-protected (Fase 10, Checkpoint 3). <see cref="EarlyCheckInRequest.ReservationId"/>/
/// <see cref="EarlyCheckInRequest.PropertyId"/> carry no physical foreign key
/// — mirrors <c>GuestStayOperationConfiguration</c>'s own opaque-Guid
/// convention. At most one <c>Pending</c> request may exist per Reservation
/// at a time (mandate cardinality rule) — enforced by a partial unique
/// index, never at the Domain layer.
/// </summary>
public sealed class EarlyCheckInRequestConfiguration : IEntityTypeConfiguration<EarlyCheckInRequest>
{
    public void Configure(EntityTypeBuilder<EarlyCheckInRequest> builder)
    {
        builder.ToTable("early_check_in_requests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(r => r.PropertyId).HasColumnName("property_id").IsRequired();

        builder.Property(r => r.RequestedCheckInAt).HasColumnName("requested_check_in_at").IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.DenialReason).HasColumnName("denial_reason").HasConversion<string>().HasMaxLength(30);

        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(r => r.DecidedAtUtc).HasColumnName("decided_at_utc");
        builder.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors GuestStayOperationConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        // Cardinality rule: at most one Pending request per Reservation.
        builder.HasIndex(r => new { r.TenantId, r.ReservationId }, "ix_early_check_in_requests_tenant_id_reservation_id_pending_unique")
            .IsUnique()
            .HasFilter("status = 'Pending'");
    }
}
