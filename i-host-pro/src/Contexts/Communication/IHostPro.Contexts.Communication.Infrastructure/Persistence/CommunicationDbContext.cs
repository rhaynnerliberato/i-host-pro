using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

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
/// Fase 11, Checkpoint 2 (AI Agent Foundation): now calls
/// <c>ModelBuilder.MapWolverineEnvelopeStorage</c> — Communication publishes
/// its first Integration Event (<c>ConversationMessageReceived</c>) and needs
/// its own <c>IDbContextOutbox&lt;CommunicationDbContext&gt;</c>, deliberately
/// deferred since Fase 9, Checkpoint 1 until a real consumer existed.
/// </summary>
public sealed class CommunicationDbContext : BaseDbContext
{
    public override string SchemaName => "communication";

    public DbSet<Message> Messages => Set<Message>();

    /// <summary>Fase 11, Checkpoint 1 (Inbound Conversation Foundation).</summary>
    public DbSet<Conversation> Conversations => Set<Conversation>();

    /// <summary>Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit).</summary>
    public DbSet<AdministratorNotificationContact> AdministratorNotificationContacts => Set<AdministratorNotificationContact>();

    public CommunicationDbContext(DbContextOptions<CommunicationDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UsePropertyAccessMode(PropertyAccessMode.PreferField);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CommunicationDbContext).Assembly);

        // Must match Program.cs's own EnrollAncillaryPostgresqlOutbox(...,
        // "communication_messaging", typeof(CommunicationDbContext)) schema literal exactly.
        modelBuilder.MapWolverineEnvelopeStorage("communication_messaging");

        base.OnModelCreating(modelBuilder);
    }
}
