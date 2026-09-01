using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>ai_agent.agent_interactions</c> — tenant-owned, RLS-protected (Fase
/// 11, Checkpoint 2). Idempotency (mandate item 19/28): a unique index on
/// <c>(tenant_id, inbound_message_id)</c> is defense-in-depth behind the
/// consumer's own lookup-before-create.
/// </summary>
public sealed class AgentInteractionConfiguration : IEntityTypeConfiguration<AgentInteraction>
{
    public void Configure(EntityTypeBuilder<AgentInteraction> builder)
    {
        builder.ToTable("agent_interactions");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(i => i.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(i => i.AgentSessionId).HasColumnName("agent_session_id").IsRequired();
        builder.Property(i => i.InboundMessageId).HasColumnName("inbound_message_id").IsRequired();
        builder.Property(i => i.Intent).HasColumnName("intent").HasMaxLength(200);
        builder.Property(i => i.Language).HasColumnName("language").HasMaxLength(10);
        builder.Property(i => i.Confidence).HasColumnName("confidence").HasColumnType("numeric(5,4)");
        builder.Property(i => i.ModelProvider).HasColumnName("model_provider").HasMaxLength(100).IsRequired();
        builder.Property(i => i.ModelName).HasColumnName("model_name").HasMaxLength(100).IsRequired();
        builder.Property(i => i.InputTokens).HasColumnName("input_tokens").IsRequired();
        builder.Property(i => i.OutputTokens).HasColumnName("output_tokens").IsRequired();
        builder.Property(i => i.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(i => i.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(i => i.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.OutboundMessageId).HasColumnName("outbound_message_id");

        builder.HasIndex(i => new { i.TenantId, i.InboundMessageId }, "ix_agent_interactions_tenant_id_inbound_message_id_unique")
            .IsUnique();
    }
}
