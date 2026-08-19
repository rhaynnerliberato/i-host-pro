using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>communication.messages</c> — tenant-owned, RLS-protected (Fase 9,
/// Checkpoint 1). The unique index on <c>idempotency_key</c> is the database
/// backstop for the Application-level idempotency check (never just
/// <c>AnyAsync</c> alone — CP1 mandate §35, mirrors ADR-018's own two-layer
/// precedent).
/// </summary>
public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(m => m.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(m => m.Channel).HasColumnName("channel").HasMaxLength(30).IsRequired();
        builder.Property(m => m.TemplateKey).HasColumnName("template_key").HasMaxLength(100).IsRequired();
        builder.Property(m => m.DestinationMasked).HasColumnName("destination_masked").HasMaxLength(30);
        builder.Property(m => m.RenderedContent).HasColumnName("rendered_content").HasColumnType("text").IsRequired();
        builder.Property(m => m.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(300).IsRequired();
        builder.Property(m => m.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(m => m.SentAtUtc).HasColumnName("sent_at_utc");
        builder.Property(m => m.FailedAtUtc).HasColumnName("failed_at_utc");
        builder.Property(m => m.FailureReason).HasColumnName("failure_reason").HasMaxLength(100);
        builder.Property(m => m.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(200);

        builder.HasIndex(m => m.IdempotencyKey).IsUnique();
        builder.HasIndex(m => new { m.TenantId, m.ReservationId });

        // Fase 9, Checkpoint 2.2 (mandate §26): a future webhook (Checkpoint
        // 2.3) will need to look up a Message by the provider's own message
        // id — indexed now since that real need is already known, never a
        // global unique (tenant-scoped, mirrors every other index here).
        builder.HasIndex(m => new { m.TenantId, m.ProviderMessageId });
    }
}
