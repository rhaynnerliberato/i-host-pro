using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_email_message_receipts</c> — tenant-owned,
/// RLS-protected. Unique on (tenant_id, graph_message_id): this is the Airbnb
/// Email Bridge's own ingestion-level idempotency, independent of
/// <c>Reservation.ExternalReservationId</c>'s reservation-level
/// deduplication (see the entity's own doc comment).
/// </summary>
public sealed class AirbnbEmailMessageReceiptConfiguration : IEntityTypeConfiguration<AirbnbEmailMessageReceipt>
{
    public void Configure(EntityTypeBuilder<AirbnbEmailMessageReceipt> builder)
    {
        builder.ToTable("airbnb_email_message_receipts");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.GraphMessageId).HasColumnName("graph_message_id").HasMaxLength(500).IsRequired();
        builder.Property(r => r.InternetMessageId).HasColumnName("internet_message_id").HasMaxLength(500);
        builder.Property(r => r.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();
        builder.Property(r => r.ProcessingStatus)
            .HasColumnName("processing_status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(r => r.DetectedEventType).HasColumnName("detected_event_type").HasMaxLength(100);
        builder.Property(r => r.ExternalReservationId).HasColumnName("external_reservation_id").HasMaxLength(200);
        builder.Property(r => r.ParserVersion).HasColumnName("parser_version").HasMaxLength(50);
        builder.Property(r => r.ProcessedAtUtc).HasColumnName("processed_at_utc");
        builder.Property(r => r.FailureReason).HasColumnName("failure_reason").HasMaxLength(500);
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.GraphMessageId }).IsUnique();
    }
}
