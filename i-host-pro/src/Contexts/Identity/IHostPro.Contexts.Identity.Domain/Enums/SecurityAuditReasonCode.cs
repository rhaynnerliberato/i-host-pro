namespace IHostPro.Contexts.Identity.Domain.Enums;

/// <summary>
/// Stable ASCII codes persisted as text in `identity.security_audit_log`
/// (Incremento 2 plan, ajuste 3) — same stability rule as
/// <see cref="SecurityAuditEventType"/>: never rename a member, only add.
///
/// Deliberately excludes any reason related to tenant resolution (e.g. an
/// unknown slug or an inactive tenant): an event that occurs before a tenant
/// is validly resolved is never persisted in this RLS-protected table at all
/// (Incremento 2 plan, ajuste 2) — such reasons belong only to structured
/// logging/telemetry, never to this enum.
/// </summary>
public enum SecurityAuditReasonCode
{
    UserNotFound = 1,
    UserBlocked = 2,
    InvalidPassword = 3,
    RefreshTokenExpired = 4,
    RefreshTokenHashMismatch = 5,
    SessionNotActive = 6,
    ConcurrentRotationGraceWindow = 7,
    LogoutRequested = 8,
    AdminRevoked = 9,

    /// <summary>
    /// A login attempt was rejected because the account was ALREADY locked
    /// out from a prior failure — distinct from the <c>AccountLockedOut</c>
    /// event type, which represents the specific attempt that caused the
    /// lockout to newly trigger (Incremento 2 plan, Etapa 9).
    /// </summary>
    AccountLocked = 10,
}
