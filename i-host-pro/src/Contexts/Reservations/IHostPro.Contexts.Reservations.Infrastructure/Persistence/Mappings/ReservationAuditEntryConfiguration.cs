using IHostPro.Contexts.Reservations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>reservations.reservation_audit_log</c> — tenant-owned, RLS-protected,
/// append-only at the database privilege level (Fase 3, Incremento 1 plan,
/// item 11) — mirrors <c>PropertyAuditEntryConfiguration</c> exactly.
/// </summary>
public sealed class ReservationAuditEntryConfiguration : IEntityTypeConfiguration<ReservationAuditEntry>
{
    public void Configure(EntityTypeBuilder<ReservationAuditEntry> builder)
    {
        builder.ToTable("reservation_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id").IsRequired();
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
