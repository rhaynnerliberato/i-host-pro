using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.cleaning_audit_log</c> — tenant-owned, RLS-protected,
/// append-only at the database privilege level (Fase 6, Incremento 1) —
/// mirrors <c>ReservationAuditEntryConfiguration</c> exactly.
/// </summary>
public sealed class CleaningAuditEntryConfiguration : IEntityTypeConfiguration<CleaningAuditEntry>
{
    public void Configure(EntityTypeBuilder<CleaningAuditEntry> builder)
    {
        builder.ToTable("cleaning_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(50).IsRequired();
        builder.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
        builder.Property(e => e.ActionCode).HasColumnName("action_code").HasMaxLength(50).IsRequired();

        builder.Property(e => e.ChangedFields)
            .HasColumnName("changed_fields")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.AggregateId, e.OccurredAt });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
    }
}
