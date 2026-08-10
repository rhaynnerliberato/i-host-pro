using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>configuration.policy_audit_log</c> — tenant-owned, RLS-protected,
/// append-only at the database privilege level (mirrors
/// <c>ReservationAuditEntryConfiguration</c> exactly).
/// </summary>
public sealed class PolicyAuditEntryConfiguration : IEntityTypeConfiguration<PolicyAuditEntry>
{
    public void Configure(EntityTypeBuilder<PolicyAuditEntry> builder)
    {
        builder.ToTable("policy_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.PolicyCode).HasColumnName("policy_code").HasMaxLength(100).IsRequired();
        builder.Property(e => e.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.ScopeReferenceId).HasColumnName("scope_reference_id");
        builder.Property(e => e.PreviousVersion).HasColumnName("previous_version");
        builder.Property(e => e.NewVersion).HasColumnName("new_version").IsRequired();

        builder.Property(e => e.PreviousValue).HasColumnName("previous_value").HasColumnType("jsonb");
        builder.Property(e => e.NewValue).HasColumnName("new_value").HasColumnType("jsonb").IsRequired();

        builder.Property(e => e.AuthorUserId).HasColumnName("author_user_id").IsRequired();
        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
        builder.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        builder.Property(e => e.Origin).HasColumnName("origin").HasMaxLength(50).IsRequired();
        builder.Property(e => e.SessionId).HasColumnName("session_id").HasMaxLength(100);
        builder.Property(e => e.IpAddress).HasColumnName("ip_address").HasMaxLength(45);

        builder.HasIndex(e => new { e.TenantId, e.PolicyCode, e.ScopeType, e.ScopeReferenceId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });
    }
}
