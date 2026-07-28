namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Refresh token issuance and rotation-concurrency parameters (Incremento 2
/// plan, ajuste 1-2). <see cref="ConcurrentRotationGraceWindow"/> is the small
/// window within which a concurrently-superseded (already <c>Rotated</c>)
/// token presented again is treated as a benign race (double submit, network
/// retry, client timeout) instead of reuse — it never revokes the Session.
/// Only a presentation outside this window, or of a token revoked for any
/// other reason, is treated as evidence of reuse. The window intentionally
/// cannot be configured large enough to meaningfully weaken reuse detection —
/// see the upper bound enforced by <see cref="RefreshTokenOptionsValidator"/>.
/// </summary>
public sealed class RefreshTokenOptions
{
    public const string SectionName = "Identity:RefreshToken";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);
    public int SecretSizeBytes { get; set; } = 32;
    public TimeSpan ConcurrentRotationGraceWindow { get; set; } = TimeSpan.FromSeconds(10);
}
