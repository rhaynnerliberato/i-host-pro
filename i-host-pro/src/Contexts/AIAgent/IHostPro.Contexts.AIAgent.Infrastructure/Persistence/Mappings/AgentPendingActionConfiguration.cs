using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>ai_agent.agent_pending_actions</c> — tenant-owned, RLS-protected (Fase
/// 11, Checkpoint 4). Gets a real foreign key to <c>agent_interactions</c>
/// (same in-BC exception <see cref="AgentToolExecutionConfiguration"/>
/// already established) — <see cref="AgentSessionId"/>, by contrast, stays
/// an opaque id, mirroring every other AI Agent entity's own reference to
/// <c>AgentSession</c>.
///
/// Partial unique index on <c>(tenant_id, agent_session_id)</c> WHERE
/// <c>status IN ('Proposed','Confirmed')</c> enforces "at most one active
/// pending action per session" (CP4 mandate item 14) at the database level —
/// defense-in-depth behind the Application-layer lookup-before-create.
/// </summary>
public sealed class AgentPendingActionConfiguration : IEntityTypeConfiguration<AgentPendingAction>
{
    public void Configure(EntityTypeBuilder<AgentPendingAction> builder)
    {
        builder.ToTable("agent_pending_actions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.AgentSessionId).HasColumnName("agent_session_id").IsRequired();
        builder.Property(e => e.ProposedByInteractionId).HasColumnName("proposed_by_interaction_id").IsRequired();
        builder.Property(e => e.ToolName).HasColumnName("tool_name").HasMaxLength(200).IsRequired();
        // text, never jsonb — this column is opaque application-level
        // storage, never queried/indexed via Postgres JSON operators;
        // jsonb would silently re-canonicalize whitespace on write, which
        // would break the exact-round-trip contract Application code
        // expects when it deserializes this string back verbatim.
        builder.Property(e => e.SanitizedArguments).HasColumnName("sanitized_arguments").HasColumnType("text").IsRequired();
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(e => e.ConfirmedAtUtc).HasColumnName("confirmed_at_utc");
        builder.Property(e => e.ExecutedAtUtc).HasColumnName("executed_at_utc");
        builder.Property(e => e.CancelledAtUtc).HasColumnName("cancelled_at_utc");

        builder.HasIndex(e => new { e.TenantId, e.AgentSessionId }, "ix_agent_pending_actions_active_per_session")
            .IsUnique()
            .HasFilter("status IN ('Proposed', 'Confirmed')");

        builder.HasOne<AgentInteraction>()
            .WithMany()
            .HasForeignKey(e => e.ProposedByInteractionId)
            .HasConstraintName("fk_agent_pending_actions_agent_interactions")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
