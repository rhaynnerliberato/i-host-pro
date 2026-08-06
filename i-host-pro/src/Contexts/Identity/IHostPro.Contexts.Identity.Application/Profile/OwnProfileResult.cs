namespace IHostPro.Contexts.Identity.Application.Profile;

/// <summary>
/// The authenticated caller's own profile (Incremento 3, Checkpoint 4) —
/// exposes only persisted, safe <see cref="Domain.User"/> fields. Deliberately
/// excludes <c>PasswordHash</c>, <c>NormalizedEmail</c>, <c>SecurityStamp</c>,
/// <c>FailedAccessCount</c>, <c>LockoutEnd</c> and any session/other-tenant
/// data (Incremento 3, Checkpoint 4, approved design).
///
/// <see cref="Permissions"/> (Fase 4, Incremento 2 — minimal contract fix)
/// carries the caller's own effective permission codes, resolved from
/// <see cref="Roles"/> via the same <c>Authorization.IPermissionReader</c>
/// <c>PermissionAuthorizationHandler</c> already uses to enforce every
/// <c>[Authorize(Policy = ...)]</c> — never a frontend-side role→permission
/// mapping. Distinct, ordinal-sorted (never the underlying reader's own
/// unspecified order). This is the frontend's sole source of truth for "what
/// can I do" (no decoded JWT, no client-side catalog cross-reference).
/// </summary>
public sealed record OwnProfileResult(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
