using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Single source of truth for the canonical refresh token wire format
/// (<c>{tenantId:N}.{tokenId:N}.{secret}</c>) shared by
/// <see cref="RefreshTokenGenerator"/> (produces it) and
/// <see cref="RefreshTokenParser"/> (accepts it) — Incremento 2 plan,
/// Etapa 7. Kept internal: nothing outside this assembly needs to know the
/// wire-format details, only the Application-facing interfaces. The overall
/// length bound itself lives in <see cref="RefreshTokenLimits"/>
/// (Identity.Application, Etapa 8) so Application-layer validators can
/// reference the exact same value without this assembly depending on
/// Infrastructure.
/// </summary>
internal static class RefreshTokenFormat
{
    public const char Separator = '.';

    /// <summary><see cref="Guid"/> "N" format: exactly 32 lowercase hex characters, no hyphens.</summary>
    public const int GuidSegmentLength = 32;

    /// <summary>See <see cref="RefreshTokenLimits.MaxTotalLength"/> for the full rationale.</summary>
    public const int MaxTotalLength = RefreshTokenLimits.MaxTotalLength;

    public static bool IsCanonicalLowercaseGuidSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length != GuidSegmentLength)
            return false;

        foreach (var c in segment)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>RFC 4648 §5 (base64url) alphabet — never '+', '/', or the '=' padding character.</summary>
    public static bool IsBase64UrlAlphabet(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty)
            return false;

        foreach (var c in segment)
        {
            var isAllowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!isAllowed)
                return false;
        }

        return true;
    }
}
