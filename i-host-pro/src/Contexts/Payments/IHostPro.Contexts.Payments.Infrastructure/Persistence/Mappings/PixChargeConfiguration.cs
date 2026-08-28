using IHostPro.Contexts.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>payments.pix_charges</c> — tenant-owned, RLS-protected (Fase 10,
/// Checkpoint 5). <see cref="PixCharge.LateCheckoutRequestId"/>/
/// <see cref="PixCharge.ReservationId"/> carry no physical foreign key —
/// mirrors <c>GuestStayOperationConfiguration</c>'s own opaque-Guid
/// convention (Payments never references GuestOperations'/Reservations'
/// schema directly). At most one ACTIVE (<c>Pending</c>) charge may exist
/// per <c>LateCheckoutRequestId</c> at a time (mandate item 14) — enforced
/// by a partial unique index, never at the Domain layer.
/// </summary>
public sealed class PixChargeConfiguration : IEntityTypeConfiguration<PixCharge>
{
    public void Configure(EntityTypeBuilder<PixCharge> builder)
    {
        builder.ToTable("pix_charges");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.LateCheckoutRequestId).HasColumnName("late_checkout_request_id").IsRequired();
        builder.Property(c => c.ReservationId).HasColumnName("reservation_id").IsRequired();

        builder.Property(c => c.Amount).HasColumnName("amount").HasColumnType("numeric(12,2)").IsRequired();
        builder.Property(c => c.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();

        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(c => c.ProviderChargeId).HasColumnName("provider_charge_id").HasMaxLength(200);

        // Sensitive operational payment data (never a credential/API key) —
        // see PixCharge's own doc comment and ADR-025. No column-level
        // encryption this checkpoint (explicit product decision) — protected
        // by RLS/tenant isolation like every other column, never logged,
        // never re-published in an event, never in a query string. 4000
        // chars comfortably covers a real PIX "copia e cola" EMV payload
        // (typically well under 512 chars) with headroom, never an
        // arbitrarily small limit.
        builder.Property(c => c.QrCodePayload).HasColumnName("qr_code_payload").HasMaxLength(4000);

        builder.Property(c => c.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();

        builder.Property(c => c.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(c => c.ConfirmedAtUtc).HasColumnName("confirmed_at_utc");
        builder.Property(c => c.FailedAtUtc).HasColumnName("failed_at_utc");
        builder.Property(c => c.ExpiredAtUtc).HasColumnName("expired_at_utc");

        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in
        // system column `xmin` — mirrors every other aggregate's own mapping.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.LateCheckoutRequestId }, "ix_pix_charges_tenant_id_late_checkout_request_id_active_unique")
            .IsUnique()
            .HasFilter("status = 'Pending'");
    }
}
