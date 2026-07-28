namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Defensive bounds on the presented refresh token string shared between
/// <c>Identity.Infrastructure</c>'s strict parser (which enforces it) and
/// this layer's request validators (which reject oversized input before it
/// ever reaches the parser) — Incremento 2 plan, Etapa 7/8. Lives here,
/// not in Infrastructure, precisely so Application-layer validators can
/// reference it without depending on Infrastructure (Architecture
/// Principles, Section 4); Infrastructure's <c>RefreshTokenFormat</c>
/// references this value instead of defining its own copy.
/// </summary>
public static class RefreshTokenLimits
{
    /// <summary>
    /// Upper bound on the *entire* presented string, independent of the
    /// currently configured <c>RefreshTokenOptions.SecretSizeBytes</c> —
    /// deliberately not tied to live configuration, so rotating that setting
    /// can never retroactively invalidate the format of an already-issued
    /// token. Comfortably larger than the longest string the canonical
    /// format can legitimately produce even at the maximum allowed secret
    /// size (64 bytes → 86 base64url characters; 32 + 1 + 32 + 1 + 86 = 152),
    /// while still bounding processing of clearly abusive input.
    /// </summary>
    public const int MaxTotalLength = 256;
}
