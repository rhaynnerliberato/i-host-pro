using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Reads <see cref="Session"/> rows for self-service listing (Incremento 3,
/// Checkpoint 4) and for the revocation cascade (Checkpoint 6+). Distinct from
/// <c>IRepository&lt;Session,Guid&gt;</c> (single lookup by primary key, used
/// by Logout/Refresh/RevokeOwnSession) — this is a list-by-criteria capability
/// none of the existing abstractions provide.
///
/// Deliberately two methods, not one with a <c>trackEntities</c> flag
/// (Incremento 3, Checkpoint 9 follow-up review): <see cref="ListActiveByUserIdAsync"/>
/// and <see cref="ListActiveForUpdateByUserIdAsync"/> have opposite tracking
/// needs that must never be conflated again — see
/// <see cref="ListActiveForUpdateByUserIdAsync"/>'s doc comment for the bug
/// this replaces (a single method with implicit tracking semantics silently
/// broke the cascade when a second caller with the opposite need was added).
/// </summary>
public interface ISessionReader
{
    /// <summary>
    /// Active sessions belonging to <paramref name="userId"/>, for read-only
    /// display (<c>ListOwnSessionsQueryHandler</c>) — the caller never mutates
    /// any returned instance. Tenant isolation needs no explicit filter here —
    /// <see cref="Session"/> implements <c>ITenantOwned</c>, so the
    /// DbContext's Global Query Filter already scopes this to the current
    /// tenant (same reasoning as <c>UserRoleReader.GetRoleCodesAsync</c>).
    /// </summary>
    Task<IReadOnlyCollection<Session>> ListActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Same filter as <see cref="ListActiveByUserIdAsync"/>, but for
    /// <c>UserSessionRevoker</c>'s revocation cascade (Incremento 3, Checkpoint
    /// 6+, reused by AssignRole/RemoveRole/Block/ChangeOwnPassword/
    /// AdminResetPassword) — the caller mutates every returned instance
    /// directly via <c>Session.Revoke(...)</c>, so it must return entities the
    /// <c>DbContext</c>'s Change Tracker is aware of. Previously the SAME
    /// method served both callers; loading it with <c>AsNoTracking()</c> was
    /// correct for the read-only caller alone and silently broke this one — a
    /// genuine PostgreSQL integration test confirmed the cascade's
    /// <c>SaveChangesAsync</c> never persisted <c>Session.Status</c>/
    /// <c>RevokedAt</c> despite events/audit/Redis all firing correctly.
    /// Split into two explicitly named methods instead of one with a
    /// <c>trackEntities</c> flag, so the tracking need is part of the
    /// contract, never implicit.
    /// </summary>
    Task<IReadOnlyCollection<Session>> ListActiveForUpdateByUserIdAsync(Guid userId, CancellationToken cancellationToken);
}
