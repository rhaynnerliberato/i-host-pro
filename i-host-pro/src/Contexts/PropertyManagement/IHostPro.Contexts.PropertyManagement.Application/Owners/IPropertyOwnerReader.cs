using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Reads <see cref="PropertyOwnerLink"/> — a separate abstraction rather
/// than <c>IRepository&lt;PropertyOwnerLink,?&gt;</c>: the link is not an
/// <see cref="IHostPro.BuildingBlocks.Domain.AggregateRoot{TId}"/> (Checkpoint 0
/// plan, item 8), so it cannot satisfy that generic constraint — mirrors
/// Identity's own <c>IUserRoleReader</c>/<c>IUserRoleWriter</c> split for
/// <c>UserRole</c>, the same kind of non-aggregate entity.
/// </summary>
public interface IPropertyOwnerReader
{
    /// <summary>
    /// True when this exact (propertyId, ownerUserId) pair is already
    /// linked (Checkpoint 5 plan, item 7) — the structural pre-check
    /// <c>LinkPropertyOwnerCommandHandler</c> runs before attempting the
    /// insert; the unique constraint itself is the authoritative guarantee
    /// for the concurrent case.
    /// </summary>
    Task<bool> ExistsAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// The tracked <see cref="PropertyOwnerLink"/> row for this exact
    /// (propertyId, ownerUserId) pair, or <c>null</c> if no such link
    /// currently exists (Checkpoint 5 plan, item 8) — the entity
    /// <see cref="IPropertyOwnerWriter.Unlink"/> needs, mirroring
    /// <c>IUserRoleReader.FindAsync</c>.
    /// </summary>
    Task<PropertyOwnerLink?> FindAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// The administrative, paginated listing of owners linked to a single
    /// property (Checkpoint 5 plan, item 9) — ordered deterministically by
    /// <c>created_at</c> then <c>owner_user_id</c>. Never queries Identity;
    /// <see cref="PropertyOwnerResult"/> carries no owner name/email/status.
    /// </summary>
    Task<PagedResult<PropertyOwnerResult>> ListByPropertyAsync(
        Guid propertyId, int page, int pageSize, CancellationToken cancellationToken);
}
