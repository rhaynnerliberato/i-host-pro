namespace IHostPro.Contexts.Identity.Domain.Enums;

/// <summary>
/// Stable ASCII codes persisted as text in `identity.security_audit_log`
/// (Incremento 2 plan, ajuste 3) — member names ARE the persisted value
/// (EF Core string conversion, see <c>SecurityAuditEntryConfiguration</c>).
/// Never rename an existing member: doing so changes the meaning of
/// already-persisted rows. Only add new members.
///
/// Matches the Integration Events already catalogued for these facts
/// (Documento 07 §13, ADR-012) — <c>UserLoggedIn</c>/<c>UserLoggedOut</c> map
/// to <see cref="LoginSucceeded"/>/<see cref="LogoutSucceeded"/> here; the
/// audit vocabulary is intentionally slightly richer than the public event
/// catalogue (e.g. it separately tracks rejection outcomes).
///
/// Members 9-16 were added in Incremento 3, Checkpoint 1 (Documento 07
/// §13.3) for the user-management events (<c>UserCreated</c>,
/// <c>UserUpdated</c>, <c>UserBlocked</c>, <c>UserUnblocked</c>,
/// <c>UserRoleAssigned</c>, <c>UserRoleRemoved</c>) — <see cref="PasswordChangedBySelf"/>/
/// <see cref="PasswordResetByAdmin"/> distinguish the two
/// <c>PasswordChanged</c> triggers the same way this enum already
/// distinguishes <see cref="LoginSucceeded"/> from <see cref="LoginRejected"/>.
/// No command writes these values yet — they exist so later checkpoints have
/// a stable code to persist.
/// </summary>
public enum SecurityAuditEventType
{
    LoginSucceeded = 1,
    LoginRejected = 2,
    AccountLockedOut = 3,
    RefreshSucceeded = 4,
    RefreshRejected = 5,
    RefreshTokenReuseDetected = 6,
    LogoutSucceeded = 7,
    SessionRevoked = 8,
    UserCreated = 9,
    UserUpdated = 10,
    UserBlocked = 11,
    UserUnblocked = 12,
    UserRoleAssigned = 13,
    UserRoleRemoved = 14,
    PasswordChangedBySelf = 15,
    PasswordResetByAdmin = 16,
}
