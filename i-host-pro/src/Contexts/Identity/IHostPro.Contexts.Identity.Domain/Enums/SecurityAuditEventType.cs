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
}
