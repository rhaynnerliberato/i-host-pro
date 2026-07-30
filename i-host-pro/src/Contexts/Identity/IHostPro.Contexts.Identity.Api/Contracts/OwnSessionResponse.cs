namespace IHostPro.Contexts.Identity.Api.Contracts;

public sealed record OwnSessionResponse(
    Guid SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    bool IsCurrent,
    string? Browser);
