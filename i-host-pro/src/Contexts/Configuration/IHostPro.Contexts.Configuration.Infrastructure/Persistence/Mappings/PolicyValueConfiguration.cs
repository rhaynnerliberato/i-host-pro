using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>configuration.policy_values</c> — tenant-owned, RLS-protected,
/// TENANT/PROPERTY scope only (Fase 5, Incremento 1 official decisions §4).
/// Append-only: no row is ever updated after insert except its
/// <c>is_current</c> flag flipping to <c>false</c> when superseded. The
/// partial unique index enforcing "only one current row per scope" is added
/// via raw SQL in the migration (<c>CREATE UNIQUE INDEX ... WHERE
/// is_current</c>), because it must COALESCE the nullable
/// <c>scope_reference_id</c> column to be effective for TENANT-scoped rows —
/// not expressible through the EF Core fluent index API.
/// </summary>
public sealed class PolicyValueConfiguration : IEntityTypeConfiguration<PolicyValue>
{
    public void Configure(EntityTypeBuilder<PolicyValue> builder)
    {
        builder.ToTable("policy_values");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(v => v.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(v => v.PolicyCode).HasColumnName("policy_code").HasMaxLength(100).IsRequired();
        builder.Property(v => v.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(v => v.ScopeReferenceId).HasColumnName("scope_reference_id");
        builder.Property(v => v.Version).HasColumnName("version").IsRequired();

        builder.Property(v => v.Value)
            .HasColumnName("value")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(v => v.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(v => v.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(v => v.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
        builder.Property(v => v.IsCurrent).HasColumnName("is_current").IsRequired();

        builder.HasOne<PolicyDefinition>()
            .WithMany()
            .HasForeignKey(v => v.PolicyCode)
            .OnDelete(DeleteBehavior.Restrict);

        // Version numbers are never reused within the same scope.
        builder.HasIndex(v => new { v.TenantId, v.PolicyCode, v.ScopeType, v.ScopeReferenceId, v.Version }).IsUnique();

        // Read path: resolving the current value at a given scope.
        builder.HasIndex(v => new { v.TenantId, v.PolicyCode, v.ScopeType, v.ScopeReferenceId, v.IsCurrent });
    }
}
