namespace IHostPro.Contexts.Identity.Application.Profile;

/// <summary>
/// The authenticated caller's own profile (Incremento 3, Checkpoint 4) —
/// exposes only persisted, safe <see cref="Domain.User"/> fields. Deliberately
/// excludes <c>PasswordHash</c>, <c>NormalizedEmail</c>, <c>SecurityStamp</c>,
/// <c>FailedAccessCount</c>, <c>LockoutEnd</c> and any session/other-tenant
/// data (Incremento 3, Checkpoint 4, approved design).
/// </summary>
public sealed record OwnProfileResult(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
