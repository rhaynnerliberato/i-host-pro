using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Reusable cascade: revokes every active session belonging to a user, and
/// every still-active refresh token under each (Incremento 3, Checkpoint 6)
/// — the shared mechanics behind any command that must force
/// re-authentication after a security-relevant change to a user (role
/// assignment/removal now; block and password change in later checkpoints,
/// Section 7: "o componente deve ser reutilizável"). Stages each revoked
/// session on <see cref="ISessionRevocationSignal"/> for its caller's
/// executor to write to <see cref="ISessionRevocationCache"/> AFTER the
/// transaction commits — never touches Redis itself. Deliberately emits no
/// Integration Event and writes no audit entry: the calling command handler
/// controls both (Section 7 — "não emitir eventos dentro do revoker").
/// </summary>
public interface IUserSessionRevoker
{
    /// <summary>
    /// Revokes every active session of (<paramref name="tenantId"/>,
    /// <paramref name="userId"/>) and every still-active refresh token under
    /// each, using <paramref name="sessionRevocationReasonCode"/>/
    /// <paramref name="refreshTokenRevocationReason"/> for the two entities'
    /// own reason fields. Returns the ids of the sessions actually revoked
    /// (empty if the user had none), in no particular order — the caller uses
    /// this to emit exactly one <c>SessionRevoked</c> event per entry, never
    /// one per refresh token.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> RevokeAllActiveSessionsAsync(
        Guid tenantId,
        Guid userId,
        string sessionRevocationReasonCode,
        RefreshTokenRevocationReason refreshTokenRevocationReason,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
