using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.security_audit_log` — tenant-owned, RLS-protected, append-only
/// (Incremento 2 plan, ajuste 1-6). `event_type`/`reason_code` are persisted
/// as text (stable ASCII codes — see the stability contract documented on
/// the enums themselves), never as free text or a bare integer.
///
/// `user_id`, `session_id` and `refresh_token_id` are intentionally NOT
/// foreign keys: this table must never lose history, be blocked, or cascade
/// as a side effect of a User/Session/RefreshToken row being removed later
/// (Incremento 2 plan, ajuste 5).
/// </summary>
public sealed class SecurityAuditEntryConfiguration : IEntityTypeConfiguration<SecurityAuditEntry>
{
    public void Configure(EntityTypeBuilder<SecurityAuditEntry> builder)
    {
        builder.ToTable("security_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();

        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.ReasonCode)
            .HasColumnName("reason_code")
            .HasConversion<string>()
            .HasMaxLength(64);

        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.UserId).HasColumnName("user_id");
        // Fase 12, CP4 — the administrator who performed the action, distinct
        // from UserId (the target). Nullable, no FK (same never-lose-history
        // reasoning as UserId/SessionId/RefreshTokenId above), never
        // backfilled for pre-existing rows.
        builder.Property(e => e.ActorId).HasColumnName("actor_id");
        builder.Property(e => e.SessionId).HasColumnName("session_id");
        builder.Property(e => e.RefreshTokenId).HasColumnName("refresh_token_id");
        builder.Property(e => e.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();

        // `tenants` is a platform catalog, never deleted in practice (only
        // Suspend/Reactivate) — a direct FK here mirrors `users.tenant_id`
        // (Incremento 1) and cannot provoke the kind of history loss this
        // table must avoid for User/Session/RefreshToken above.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .HasPrincipalKey(t => t.Id)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.ActorId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredAt });
    }
}
