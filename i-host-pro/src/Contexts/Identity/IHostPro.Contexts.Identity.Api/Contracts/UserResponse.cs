namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>Shared by the create response, each list item, and the detail response (Incremento 3, Checkpoint 5).</summary>
public sealed record UserResponse(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
