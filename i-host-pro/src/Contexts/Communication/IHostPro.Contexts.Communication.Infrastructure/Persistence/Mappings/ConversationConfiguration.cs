using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>communication.conversations</c> — tenant-owned, RLS-protected (Fase 11,
/// Checkpoint 1). The unique index on <c>(tenant_id, reservation_id,
/// channel)</c> enforces the approved cardinality: one Conversation per
/// Reservation+Channel (mandate item 19) — no archive/reopen semantics exist
/// yet, so this is a plain unique index, never a partial one filtered by
/// status.
/// </summary>
public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(c => c.Channel).HasColumnName("channel").HasMaxLength(30).IsRequired();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(c => c.LastMessageAtUtc).HasColumnName("last_message_at_utc").IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.ReservationId, c.Channel }).IsUnique();
    }
}
