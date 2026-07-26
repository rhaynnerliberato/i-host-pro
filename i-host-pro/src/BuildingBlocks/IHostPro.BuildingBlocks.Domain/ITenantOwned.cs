namespace IHostPro.BuildingBlocks.Domain;

/// <summary>
/// Implemented by every entity that belongs to a single Tenant. BaseDbContext
/// (IHostPro.BuildingBlocks.Infrastructure) scans for this interface to apply a
/// global EF Core query filter automatically to every such entity, guaranteeing
/// that no query can accidentally cross tenant boundaries
/// (Architecture Principles, Section 7). Declared in the Domain project — not
/// Infrastructure — because concrete Entities/AggregateRoots in each Bounded
/// Context's Domain layer are the ones implementing it, and Domain must never
/// depend on Infrastructure.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
