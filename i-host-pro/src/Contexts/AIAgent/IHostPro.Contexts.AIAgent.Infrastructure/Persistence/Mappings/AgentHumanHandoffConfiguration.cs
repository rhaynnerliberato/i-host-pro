using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>ai_agent.agent_human_handoffs</c> — tenant-owned, RLS-protected (Fase
/// 11, Checkpoint 6). Gets a real foreign key to <c>agent_sessions</c> — same
/// in-BC exception <see cref="AgentPendingActionConfiguration"/> already
/// established for <c>agent_interactions</c>, since both tables live in the
/// same <c>ai_agent</c> schema/Bounded Context.
///
/// Partial unique index on <c>(tenant_id, agent_session_id)</c> WHERE
/// <c>status IN ('Requested','Notified')</c> enforces "at most one active
/// handoff per session" (CP6 mandate item 10) at the database level —
/// defense-in-depth behind the Application-layer lookup-before-create.
/// </summary>
public sealed class AgentHumanHandoffConfiguration : IEntityTypeConfiguration<AgentHumanHandoff>
{
    public void Configure(EntityTypeBuilder<AgentHumanHandoff> builder)
    {
        builder.ToTable("agent_human_handoffs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.AgentSessionId).HasColumnName("agent_session_id").IsRequired();
        builder.Property(e => e.ReasonCode).HasColumnName("reason_code").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.RequestedAtUtc).HasColumnName("requested_at_utc").IsRequired();
        builder.Property(e => e.NotificationAttemptedAtUtc).HasColumnName("notification_attempted_at_utc");
        builder.Property(e => e.NotifiedAtUtc).HasColumnName("notified_at_utc");
        builder.Property(e => e.NotificationFailureCode).HasColumnName("notification_failure_code").HasMaxLength(200);
        builder.Property(e => e.ResumedAtUtc).HasColumnName("resumed_at_utc");
        builder.Property(e => e.ResumedByActorId).HasColumnName("resumed_by_actor_id");

        builder.HasIndex(e => new { e.TenantId, e.AgentSessionId }, "ix_agent_human_handoffs_active_per_session")
            .IsUnique()
            .HasFilter("status IN ('Requested', 'Notified')");

        builder.HasOne<AgentSession>()
            .WithMany()
            .HasForeignKey(e => e.AgentSessionId)
            .HasConstraintName("fk_agent_human_handoffs_agent_sessions")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
