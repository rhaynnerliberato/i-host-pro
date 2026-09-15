using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_email_sync_states</c> — tenant-owned,
/// RLS-protected. One cursor per (tenant, mailbox connection, mail folder) —
/// the unique index allows tracking more than one folder later without a
/// schema change, even though the MVP only ever populates one row per
/// connection. <c>delta_link</c> is an opaque, potentially long Microsoft
/// Graph value — mapped as unbounded <c>text</c>, never a bounded varchar.
/// </summary>
public sealed class AirbnbEmailSyncStateConfiguration : IEntityTypeConfiguration<AirbnbEmailSyncState>
{
    public void Configure(EntityTypeBuilder<AirbnbEmailSyncState> builder)
    {
        builder.ToTable("airbnb_email_sync_states");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(s => s.MailboxConnectionId).HasColumnName("mailbox_connection_id").IsRequired();
        builder.Property(s => s.MailFolderId).HasColumnName("mail_folder_id").HasMaxLength(200).IsRequired();
        builder.Property(s => s.DeltaLink).HasColumnName("delta_link").HasColumnType("text");
        builder.Property(s => s.LastSuccessfulSyncAtUtc).HasColumnName("last_successful_sync_at_utc");
        builder.Property(s => s.LastAttemptAtUtc).HasColumnName("last_attempt_at_utc");
        builder.Property(s => s.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(200);
        builder.Property(s => s.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(s => new { s.TenantId, s.MailboxConnectionId, s.MailFolderId }).IsUnique();

        builder.HasOne<AirbnbEmailMailboxConnection>()
            .WithMany()
            .HasForeignKey(s => s.MailboxConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
