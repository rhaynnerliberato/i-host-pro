namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Exposes the configured concurrent-rotation grace window to
/// <c>RefreshTokenCommandHandler</c> (Incremento 2 plan, Etapa 10) without
/// that Application-layer class depending on Identity.Infrastructure's
/// <c>RefreshTokenOptions</c> directly — mirrors how
/// <see cref="GeneratedRefreshToken.ExpiresAt"/> already crosses this same
/// boundary (Etapa 7/9). Implemented in Infrastructure, which legitimately
/// has <c>IOptions&lt;RefreshTokenOptions&gt;</c> access.
/// </summary>
public interface IRefreshTokenRotationPolicy
{
    /// <summary>
    /// The window within which a concurrently-superseded (already
    /// <c>Rotated</c>) token presented again is treated as a benign race
    /// rather than reuse — see <c>RefreshTokenOptions.ConcurrentRotationGraceWindow</c>.
    /// </summary>
    TimeSpan ConcurrentRotationGraceWindow { get; }
}
