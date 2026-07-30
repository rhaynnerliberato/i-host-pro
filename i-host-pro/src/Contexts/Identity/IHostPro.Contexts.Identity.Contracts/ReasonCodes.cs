namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Stable ASCII values for <see cref="LoginFailed.ReasonCode"/> (Documento 07
/// §13.1). Part of the public contract — never rename an existing value,
/// only add new ones.
/// </summary>
public static class LoginFailedReasonCodes
{
    public const string UserNotFound = "user_not_found";
    public const string UserBlocked = "user_blocked";
    public const string InvalidPassword = "invalid_password";
    public const string AccountLocked = "account_locked";
}

/// <summary>
/// Stable ASCII values for <see cref="AccountLockedOut.ReasonCode"/>
/// (Documento 07 §13.1). Part of the public contract — never rename an
/// existing value, only add new ones.
/// </summary>
public static class AccountLockedOutReasonCodes
{
    public const string MaxFailedAttempts = "max_failed_attempts";
}

/// <summary>
/// Stable ASCII values for <see cref="SessionRevoked.ReasonCode"/>
/// (Documento 07 §13.1, §13.3). Part of the public contract — never rename an
/// existing value, only add new ones.
///
/// <c>UserBlocked</c>, <c>RolesChanged</c>, <c>PasswordChanged</c> and
/// <c>UserRequestedRevocation</c> are reserved as of Incremento 3, Checkpoint
/// 1 — the vocabulary exists so later checkpoints (block/unblock, role
/// assignment, password change, self-service session revocation) can
/// reference a stable code, but no command publishes
/// <see cref="SessionRevoked"/> with any of these four yet.
/// </summary>
public static class SessionRevokedReasonCodes
{
    public const string LogoutRequested = "logout_requested";
    public const string RefreshTokenReuseDetected = "refresh_token_reuse_detected";
    public const string UserBlocked = "user_blocked";
    public const string RolesChanged = "roles_changed";
    public const string PasswordChanged = "password_changed";
    public const string UserRequestedRevocation = "user_requested_revocation";
}

/// <summary>
/// Stable ASCII values for <see cref="PasswordChanged.ChangeType"/>
/// (Documento 07 §13.3). Part of the public contract — never rename an
/// existing value, only add new ones.
/// </summary>
public static class PasswordChangeTypeCodes
{
    public const string Self = "self";
    public const string AdminReset = "admin_reset";
}
