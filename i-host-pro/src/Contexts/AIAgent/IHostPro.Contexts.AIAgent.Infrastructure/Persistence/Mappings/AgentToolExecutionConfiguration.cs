using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>ai_agent.agent_tool_executions</c> — tenant-owned, RLS-protected (Fase
/// 11, Checkpoint 3). Unlike <see cref="AgentInteraction"/>'s own reference to
/// <see cref="AgentSession"/> (opaque id, no database FK), this table gets a
/// real foreign key to <c>agent_interactions</c> because both live in the
/// same <c>ai_agent</c> schema/Bounded Context — the checkpoint's own mandate
/// (item 8) authorizes an in-BC FK, as opposed to the cross-context FKs that
/// remain forbidden everywhere else.
/// </summary>
public sealed class AgentToolExecutionConfiguration : IEntityTypeConfiguration<AgentToolExecution>
{
    public void Configure(EntityTypeBuilder<AgentToolExecution> builder)
    {
        builder.ToTable("agent_tool_executions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(e => e.AgentInteractionId).HasColumnName("agent_interaction_id").IsRequired();
        builder.Property(e => e.ToolName).HasColumnName("tool_name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(e => e.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(e => e.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.DurationMs).HasColumnName("duration_ms");
        builder.Property(e => e.FailureCode).HasColumnName("failure_code").HasMaxLength(100);

        builder.HasIndex(e => new { e.TenantId, e.AgentInteractionId }, "ix_agent_tool_executions_tenant_id_agent_interaction_id");

        builder.HasOne<AgentInteraction>()
            .WithMany()
            .HasForeignKey(e => e.AgentInteractionId)
            .HasConstraintName("fk_agent_tool_executions_agent_interactions")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
