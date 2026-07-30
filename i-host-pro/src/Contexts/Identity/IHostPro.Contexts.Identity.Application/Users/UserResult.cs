namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// A user as seen by an Administrator (Incremento 3, Checkpoint 5) — the
/// single shape shared by <c>CreateUserCommand</c>'s response, each item of
/// <c>ListUsersQuery</c>'s page, and <c>GetUserByIdQuery</c>'s result, since
/// all three expose exactly the same approved field set. Deliberately
/// excludes <c>PasswordHash</c>, <c>NormalizedEmail</c>, <c>SecurityStamp</c>,
/// <c>TenantId</c> and any session/authentication data (Incremento 3,
/// Checkpoint 5, Section 6).
/// </summary>
public sealed record UserResult(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
