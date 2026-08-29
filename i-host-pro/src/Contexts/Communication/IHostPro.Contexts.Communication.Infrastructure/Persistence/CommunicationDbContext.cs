using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

/// <summary>
/// The Communication Bounded Context's own DbContext, owning the
/// <c>communication</c> PostgreSQL schema (Fase 9, Checkpoint 1). Inherits
/// the mandatory tenant Global Query Filter from <see cref="BaseDbContext"/>
/// for <see cref="Message"/> (implements <see cref="IHostPro.BuildingBlocks.Domain.ITenantOwned"/>);
/// Row-Level Security is applied at the database level as a second,
/// independent layer of defense — mirrors every other Bounded Context's own
/// DbContext exactly.
///
/// Deliberately does NOT call <c>ModelBuilder.MapWolverineEnvelopeStorage</c>
/// — Communication publishes no Integration Event of its own this checkpoint
/// and needs no <c>IDbContextOutbox&lt;CommunicationDbContext&gt;</c> (see
/// <see cref="Application.ICommunicationTransactionExecutor"/>'s own doc
/// comment) — the first Bounded Context's DbContext in this codebase that
/// does not need this mapping.
/// </summary>
public sealed class CommunicationDbContext : BaseDbContext
{
    public override string SchemaName => "communication";

    public DbSet<Message> Messages => Set<Message>();

    /// <summary>Fase 11, Checkpoint 1 (Inbound Conversation Foundation).</summary>
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public CommunicationDbContext(DbContextOptions<CommunicationDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CommunicationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
