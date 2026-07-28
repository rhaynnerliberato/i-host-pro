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
/// (Documento 07 §13.1). Part of the public contract — never rename an
/// existing value, only add new ones.
/// </summary>
public static class SessionRevokedReasonCodes
{
    public const string LogoutRequested = "logout_requested";
    public const string RefreshTokenReuseDetected = "refresh_token_reuse_detected";
}
