using IHostPro.Contexts.GuestOperations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>guest_operations.guest_stay_operation_audit_log</c> — tenant-owned,
/// RLS-protected, append-only at the database privilege level (Fase 12,
/// Checkpoint 4 — Guest Access Durable Audit Decision Gate) — mirrors
/// <c>ReservationAuditEntryConfiguration</c> exactly.
/// </summary>
public sealed class GuestStayOperationAuditEntryConfiguration : IEntityTypeConfiguration<GuestStayOperationAuditEntry>
{
    public void Configure(EntityTypeBuilder<GuestStayOperationAuditEntry> builder)
    {
        builder.ToTable("guest_stay_operation_audit_log");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.GuestStayOperationId).HasColumnName("guest_stay_operation_id").IsRequired();
        builder.Property(e => e.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(e => e.ActorType).HasColumnName("actor_type").HasMaxLength(20).IsRequired();
        builder.Property(e => e.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.GuestStayOperationId, e.OccurredAtUtc });
        builder.HasIndex(e => new { e.TenantId, e.OccurredAtUtc });

        // Deliberately no foreign key to guest_stay_operations: mirrors
        // ReservationAuditEntry/SecurityAuditEntry's own established
        // precedent — an audit row must never be deleted just because the
        // aggregate it references is (there is no delete path for
        // GuestStayOperation today, but the absence of an FK is the same
        // "audit trail survives regardless" discipline this codebase already
        // applies everywhere else).
    }
}
