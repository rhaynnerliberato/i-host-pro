using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Stages a <see cref="UserRole"/> for persistence within the current
/// tenant-aware transaction (Incremento 3, Checkpoint 5) — the write-side
/// counterpart to <see cref="IUserRoleReader"/>. A separate abstraction
/// rather than <c>IRepository&lt;UserRole,?&gt;</c>: <see cref="UserRole"/> is
/// not an <see cref="IHostPro.BuildingBlocks.Domain.AggregateRoot{TId}"/> (no
/// single-column primary key of its own), so it cannot satisfy
/// <c>IRepository&lt;TAggregate,TId&gt;</c>'s generic constraint — the same
/// reasoning that already gives <see cref="ISecurityAuditWriter"/> its own
/// narrow, single-purpose interface instead of a generic repository.
/// </summary>
public interface IUserRoleWriter
{
    void Assign(UserRole userRole);

    /// <summary>
    /// Stages removal of an already-tracked <see cref="UserRole"/> row
    /// (Incremento 3, Checkpoint 6) — callers fetch it via
    /// <see cref="IUserRoleReader.FindAsync"/> first, never construct one to
    /// remove: <see cref="UserRole"/>'s only public constructor requires
    /// <c>AssignedAt</c>/<c>AssignedByUserId</c>, which a removal has no
    /// business fabricating.
    /// </summary>
    void Remove(UserRole userRole);
}
