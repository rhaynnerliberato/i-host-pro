namespace IHostPro.Contexts.Identity.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Identity &amp; Access commands (Incremento 3 plan). Used as
/// <see cref="IHostPro.BuildingBlocks.Domain.Error.Code"/> — deliberately
/// never paired with a hardcoded <c>Error.Message</c> here: the code is
/// stable and part of the contract, while the message is a presentation
/// concern that may be localized independently later, exactly as the
/// existing <c>new Error("Tenant.NotFound", "...")</c> call sites already
/// separate the two.
///
/// Reserved as of Incremento 3, Checkpoint 1; <see cref="SessionNotOwnedByUser"/>
/// and <see cref="AuthenticatedUserNotFound"/> are raised starting Checkpoint
/// 4, <see cref="EmailAlreadyInUse"/>/<see cref="RoleNotFound"/>/
/// <see cref="UserNotFound"/> starting Checkpoint 5,
/// <see cref="RoleAlreadyAssigned"/>/<see cref="RoleNotAssigned"/>/
/// <see cref="UserMustHaveAtLeastOneRole"/>/<see cref="LastActiveAdministrator"/>
/// starting Checkpoint 6, <see cref="UserAlreadyBlocked"/>/<see cref="UserAlreadyActive"/>
/// starting Checkpoint 7, <see cref="NoChangesProvided"/>/
/// <see cref="UserConcurrencyConflict"/> starting Checkpoint 8, and
/// <see cref="AdminCannotResetOwnPassword"/>/<see cref="InvalidCurrentPassword"/>/
/// <see cref="NewPasswordMustDiffer"/> starting Checkpoint 9 (own-password
/// change and administrative reset). <see cref="AuthenticatedUserNotFound"/>
/// is also reused by Checkpoint 9's own-password change for the same
/// defensive case as Checkpoint 4 (the caller's user row is unusable despite
/// an otherwise validly authenticated access token — here, blocked rather
/// than deleted, since there is still no user deletion).
/// </summary>
public static class IdentityErrorCodes
{
    /// <summary>Assigning a role a user already holds (no state change would occur).</summary>
    public const string RoleAlreadyAssigned = "Identity.RoleAlreadyAssigned";

    /// <summary>Removing a role a user does not hold (no state change would occur).</summary>
    public const string RoleNotAssigned = "Identity.RoleNotAssigned";

    /// <summary>
    /// Removing a user's only remaining role, which would leave them with
    /// none — rejected outright (Incremento 3, Checkpoint 6): a role swap
    /// must assign the new role before removing the old one.
    /// </summary>
    public const string UserMustHaveAtLeastOneRole = "Identity.UserMustHaveAtLeastOneRole";

    /// <summary>Blocking a user whose status is already Blocked (no state change would occur).</summary>
    public const string UserAlreadyBlocked = "Identity.UserAlreadyBlocked";

    /// <summary>Unblocking a user whose status is already Active (no state change would occur).</summary>
    public const string UserAlreadyActive = "Identity.UserAlreadyActive";

    /// <summary>
    /// Blocking, or removing the ADMIN role from, the tenant's last remaining
    /// active Administrator — rejected outright by the last-Administrator
    /// guard (Incremento 3 planning).
    /// </summary>
    public const string LastActiveAdministrator = "Identity.LastActiveAdministrator";

    /// <summary>
    /// An Administrator attempted to use the admin-reset-password flow on
    /// their own account — must use the self-service change-password flow
    /// instead, which requires the current password (Incremento 3 planning).
    /// </summary>
    public const string AdminCannotResetOwnPassword = "Identity.AdminCannotResetOwnPassword";

    /// <summary>The current password supplied for a self-service password change does not match.</summary>
    public const string InvalidCurrentPassword = "Identity.InvalidCurrentPassword";

    /// <summary>
    /// A self-service password change or an administrative reset supplied a
    /// new password that is identical to the user's current one (Incremento
    /// 3, Checkpoint 9) — checked by verifying the new password against the
    /// CURRENT hash via the same password-verification abstraction used for
    /// login, never by comparing hashes directly (they are salted).
    /// </summary>
    public const string NewPasswordMustDiffer = "Identity.NewPasswordMustDiffer";

    /// <summary>
    /// A self-service session action (e.g. revoke) targeted a session that
    /// does not belong to the authenticated user/tenant.
    /// </summary>
    public const string SessionNotOwnedByUser = "Identity.SessionNotOwnedByUser";

    /// <summary>
    /// The user identified by an otherwise validly authenticated access
    /// token's <c>sub</c> claim no longer exists in the current tenant
    /// (Incremento 3, Checkpoint 4) — a defensive case, not a rule any
    /// command in this codebase can currently trigger (there is no user
    /// deletion yet).
    /// </summary>
    public const string AuthenticatedUserNotFound = "Identity.AuthenticatedUserNotFound";

    /// <summary>
    /// Creating a user whose email (compared normalized, within the same
    /// tenant) is already in use — the PostgreSQL unique index on
    /// <c>(tenant_id, normalized_email)</c> is the authoritative guarantee;
    /// this code is what a caught unique-violation is translated to
    /// (Incremento 3, Checkpoint 5).
    /// </summary>
    public const string EmailAlreadyInUse = "Identity.EmailAlreadyInUse";

    /// <summary>Creating a user with a role code that does not exist in the persisted catalog (Incremento 3, Checkpoint 5).</summary>
    public const string RoleNotFound = "Identity.RoleNotFound";

    /// <summary>
    /// An administrative lookup (<c>GET /api/v1/users/{userId}</c>) for a
    /// user id that does not exist, or that belongs to a different tenant —
    /// the two are indistinguishable by design (Incremento 3, Checkpoint 5).
    /// Distinct from <see cref="AuthenticatedUserNotFound"/>, which is about
    /// the CALLER's own (self-service) record.
    /// </summary>
    public const string UserNotFound = "Identity.UserNotFound";

    /// <summary>
    /// <c>PATCH /api/v1/users/{userId}</c> called with neither <c>fullName</c>
    /// nor <c>email</c> provided — rejected before any lookup, since there is
    /// nothing to change (Incremento 3, Checkpoint 8).
    /// </summary>
    public const string NoChangesProvided = "Identity.NoChangesProvided";

    /// <summary>
    /// <c>PATCH /api/v1/users/{userId}</c> lost a genuine optimistic
    /// concurrency race on the <c>Users</c> row's <c>xmin</c> token to
    /// another concurrent update of the SAME user — translated from a caught
    /// <c>DbUpdateConcurrencyException</c>, never retried automatically
    /// (Incremento 3, Checkpoint 8, Section 6: "não refazer automaticamente a
    /// alteração com dados possivelmente obsoletos").
    /// </summary>
    public const string UserConcurrencyConflict = "Identity.UserConcurrencyConflict";
}
