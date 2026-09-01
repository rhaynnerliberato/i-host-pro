using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>ai_agent.agent_sessions</c> — tenant-owned, RLS-protected (Fase 11,
/// Checkpoint 2). At most one OPEN session — <see cref="AgentSessionStatus.Active"/>
/// or (Fase 11, Checkpoint 6) <see cref="AgentSessionStatus.Escalated"/> —
/// may exist per <c>(tenant_id, conversation_id)</c> at a time (governance
/// resolution item 12/27, widened by CP6's own suspended-session guard) —
/// enforced by a partial unique index, mirrors <c>PixChargeConfiguration</c>'s
/// own "at most one Pending" pattern exactly. Widening the filter (rather
/// than adding a second index) keeps a single source of truth for "does an
/// open session already exist" — <see cref="Application.AgentSessionResolver"/>
/// must never create a second session for a Conversation while one is
/// Escalated, exactly as it already never does while one is Active.
/// Completed sessions are never constrained — multiple historical sessions
/// per Conversation are explicitly allowed.
/// </summary>
public sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("agent_sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(s => s.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(s => s.ReservationId).HasColumnName("reservation_id").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Language).HasColumnName("language").HasMaxLength(10);
        builder.Property(s => s.Intent).HasColumnName("intent").HasMaxLength(200);

        // Normalized 0..1, governance resolution item 6/7 — decimal, never
        // double/float (binary floating point cannot exactly represent every
        // decimal fraction a provider might return).
        builder.Property(s => s.Confidence).HasColumnName("confidence").HasColumnType("numeric(5,4)");

        builder.Property(s => s.ModelProvider).HasColumnName("model_provider").HasMaxLength(100);
        builder.Property(s => s.ModelName).HasColumnName("model_name").HasMaxLength(100);
        builder.Property(s => s.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(s => s.LastInteractionAtUtc).HasColumnName("last_interaction_at_utc");
        builder.Property(s => s.EndedAtUtc).HasColumnName("ended_at_utc");

        builder.HasIndex(s => new { s.TenantId, s.ConversationId }, "ix_agent_sessions_tenant_id_conversation_id_active_unique")
            .IsUnique()
            .HasFilter("status IN ('Active', 'Escalated')");
    }
}
