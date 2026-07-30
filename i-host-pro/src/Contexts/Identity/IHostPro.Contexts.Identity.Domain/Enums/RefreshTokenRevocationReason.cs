namespace IHostPro.Contexts.Identity.Domain.Enums;

/// <summary>
/// Same stability rule as <see cref="SecurityAuditReasonCode"/>: never rename
/// or renumber an existing member, only add new ones — this is persisted on
/// <c>RefreshToken.RevocationReason</c>.
///
/// Members 6-9 were added in Incremento 3, Checkpoint 1 (approved decision:
/// each cascading-revocation cause gets its own value, rather than
/// overloading <see cref="AdminRevoked"/> for causes it was never meant to
/// represent). No command sets these values yet — they exist so the
/// user-management/session-management cascades introduced later in
/// Incremento 3 have a stable code to persist. A token found revoked with any
/// of these reasons falls into <c>RefreshTokenCommandHandler</c>'s existing
/// catch-all branch (treated as reuse) until that handler is extended
/// alongside the checkpoint that starts setting the value.
/// </summary>
public enum RefreshTokenRevocationReason
{
    Rotated = 1,
    LogoutRequested = 2,
    ReuseDetected = 3,
    AdminRevoked = 4,
    Expired = 5,
    UserBlocked = 6,
    RolesChanged = 7,
    PasswordChanged = 8,
    UserRequestedRevocation = 9,
}
