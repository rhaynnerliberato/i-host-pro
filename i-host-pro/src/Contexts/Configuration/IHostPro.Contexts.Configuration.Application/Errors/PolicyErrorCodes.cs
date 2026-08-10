namespace IHostPro.Contexts.Configuration.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Configuration &amp; Policy commands/queries — mirrors
/// <c>ReservationsErrorCodes</c>'s own convention exactly. Fase 5, Incremento
/// 1 official decisions §8 requires the administrative API to distinguish
/// exactly these ProblemDetails outcomes (plus the generic
/// <c>validation_error</c> fallback produced by FluentValidation, which has
/// no dedicated code here).
/// </summary>
public static class PolicyErrorCodes
{
    /// <summary>The route's <c>policyCode</c> does not match any entry in the seeded catalog.</summary>
    public const string PolicyNotFound = "Configuration.PolicyNotFound";

    /// <summary>
    /// The submitted value fails structural or technical validation against
    /// its policy code's catalog shape (§3) — malformed JSON, a field with
    /// the wrong type, or (for <c>LATE_CHECKOUT</c>) an out-of-range
    /// <c>chargeValue</c>/a <c>chargeValue</c> present or absent inconsistent
    /// with <c>chargeType</c>. Never a business-rule concern (e.g. anything
    /// about Reservations) — this increment defines no such rule.
    /// </summary>
    public const string InvalidPolicyValue = "Configuration.InvalidPolicyValue";

    /// <summary>
    /// The requested scope is structurally invalid for this operation — an
    /// unrecognized <c>scopeType</c>, or <c>Property</c> scope submitted
    /// without a <c>propertyId</c>.
    /// </summary>
    public const string ScopeNotSupported = "Configuration.ScopeNotSupported";

    /// <summary>The policy code is real, but no <c>PolicyValue</c> exists at the exact requested scope.</summary>
    public const string PolicyNotConfigured = "Configuration.PolicyNotConfigured";

    /// <summary>
    /// A new version was submitted with an <c>expectedVersion</c> that no
    /// longer matches the scope's actual current version — either a stale
    /// read, or a genuine concurrent write lost the race (caught via the
    /// database's own partial unique index, never retried automatically).
    /// </summary>
    public const string VersionConflict = "Configuration.VersionConflict";

    /// <summary>
    /// <c>scopeType == Global</c> submitted to a tenant-facing write
    /// operation — official decision 2.2: GLOBAL is read-only in the MVP,
    /// no tenant-facing command may ever alter it. Distinct from
    /// <see cref="ScopeNotSupported"/>: the request is well-formed, but
    /// absolutely prohibited regardless of the caller's permissions.
    /// </summary>
    public const string Forbidden = "Configuration.Forbidden";
}
