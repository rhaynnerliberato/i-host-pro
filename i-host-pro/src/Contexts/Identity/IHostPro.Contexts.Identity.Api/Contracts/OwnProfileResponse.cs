namespace IHostPro.Contexts.Identity.Api.Contracts;

public sealed record OwnProfileResponse(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
