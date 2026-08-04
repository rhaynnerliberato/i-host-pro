using System.Text.Json;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.property_audit_log` — tenant-owned, RLS-protected,
/// append-only (Checkpoint 0/1 plan, item 11). <c>actor_user_id</c> and
/// <c>aggregate_id</c> are intentionally NOT foreign keys — mirrors
/// <c>SecurityAuditEntryConfiguration</c>: this table must never lose
/// history or be blocked/cascaded by a Property/Condominium/link row being
/// removed later. Append-only is additionally enforced at the database
/// level: the application role is granted SELECT/INSERT only on this table,
/// never UPDATE/DELETE (see the initial migration's grants).
/// </summary>
public sealed class PropertyAuditEntryConfiguration : IEntityTypeConfiguration<PropertyAuditEntry>
{
    public void Configure(EntityTypeBuilder<PropertyAuditEntry> builder)
    {
        builder.ToTable("property_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(50).IsRequired();
        builder.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
        builder.Property(e => e.ActionCode).HasColumnName("action_code").HasMaxLength(50).IsRequired();

        builder.Property(e => e.ChangedFields)
            .HasConversion(
                fields => JsonSerializer.Serialize(fields, JsonOptions),
                json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? Array.Empty<string>())
            .HasColumnName("changed_fields")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.AggregateId, e.OccurredAt });
    }

    private static readonly JsonSerializerOptions JsonOptions = new();
}
