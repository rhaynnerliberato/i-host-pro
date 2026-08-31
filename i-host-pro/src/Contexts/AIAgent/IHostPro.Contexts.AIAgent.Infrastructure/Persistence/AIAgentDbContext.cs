using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.AIAgent.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

/// <summary>
/// The AI Agent Bounded Context's own DbContext, owning the <c>ai_agent</c>
/// PostgreSQL schema (Fase 11, Checkpoint 2 — AI Agent Foundation). Inherits
/// the mandatory tenant Global Query Filter from <see cref="BaseDbContext"/>
/// for <see cref="AgentSession"/>/<see cref="AgentInteraction"/> (both
/// implement <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>);
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors every other Bounded Context's own
/// DbContext exactly.
/// </summary>
public sealed class AIAgentDbContext : BaseDbContext
{
    public override string SchemaName => "ai_agent";

    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<AgentInteraction> AgentInteractions => Set<AgentInteraction>();
    public DbSet<AgentToolExecution> AgentToolExecutions => Set<AgentToolExecution>();

    public AIAgentDbContext(DbContextOptions<AIAgentDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AIAgentDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "ai_agent_messaging", typeof(AIAgentDbContext)) schema literal
        // exactly (mandate item 29) — needed so IDbContextOutbox<AIAgentDbContext>
        // can resolve inside the Worker's Wolverine consumer, the same
        // empirically-confirmed requirement as every other write-capable
        // Bounded Context, regardless of whether AI Agent publishes any
        // Integration Event of its own yet (it does not, this checkpoint).
        modelBuilder.MapWolverineEnvelopeStorage("ai_agent_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
