namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// <see cref="Permissions"/> (Fase 4, Incremento 2 — minimal contract fix)
/// carries the caller's own effective permission codes (distinct,
/// ordinal-sorted), resolved server-side from <see cref="Roles"/> — the
/// frontend's sole source of truth for its own authorization decisions
/// (never a decoded JWT, never a client-side role→permission mapping).
/// <see cref="Roles"/> keeps its existing meaning and is never removed.
/// </summary>
public sealed record OwnProfileResponse(
    Guid Id,
    string FullName,
    string Email,
    string Status,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);
