using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <inheritdoc cref="IRefreshTokenParser"/>
public sealed class RefreshTokenParser : IRefreshTokenParser
{
    public bool TryParse(string? presentedToken, out ParsedRefreshToken parsed)
    {
        parsed = default;

        if (string.IsNullOrEmpty(presentedToken))
            return false;

        if (presentedToken.Length > RefreshTokenFormat.MaxTotalLength)
            return false;

        // No trimming, casing normalization, or other adjustment — the
        // string is validated exactly as received. A presented value that
        // needed "cleaning up" to parse is not a valid token; silently
        // tolerating a variant here would let two different strings be
        // treated as the same logical token before hashing ever happens.
        var segments = presentedToken.Split(RefreshTokenFormat.Separator);
        if (segments.Length != 3)
            return false;

        var tenantSegment = segments[0];
        var tokenIdSegment = segments[1];
        var secretSegment = segments[2];

        if (!RefreshTokenFormat.IsCanonicalLowercaseGuidSegment(tenantSegment))
            return false;

        if (!RefreshTokenFormat.IsCanonicalLowercaseGuidSegment(tokenIdSegment))
            return false;

        // The secret segment's charset is validated (base64url, no padding)
        // but never decoded here — this step extracts only the two
        // selectors (tenantId, tokenId); the secret is relevant only to
        // hashing, performed separately over the *entire* original string.
        if (!RefreshTokenFormat.IsBase64UrlAlphabet(secretSegment))
            return false;

        if (!Guid.TryParseExact(tenantSegment, "N", out var tenantId))
            return false;

        if (!Guid.TryParseExact(tokenIdSegment, "N", out var tokenId))
            return false;

        parsed = new ParsedRefreshToken(tenantId, tokenId);
        return true;
    }
}
