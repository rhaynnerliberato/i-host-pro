using System.Security.Cryptography;
using System.Text;
using IHostPro.Contexts.ExternalIntegrations.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta's own webhook security contract (Fase 9, Checkpoint 2.3.1 — ADR-022):
/// GET verification (<c>hub.mode</c>/<c>hub.verify_token</c>/
/// <c>hub.challenge</c>) and POST <c>X-Hub-Signature-256</c>
/// (<c>sha256=&lt;hex&gt;</c>, HMAC-SHA256 over the exact raw body bytes —
/// never a re-serialized/re-parsed version, since HMAC is sensitive to every
/// byte). Stateless — never reads configuration itself; credential values
/// are resolved by <see cref="IWhatsAppWebhookCredentialProvider"/> and
/// passed in by the caller.
///
/// Both comparisons use <see cref="CryptographicOperations.FixedTimeEquals"/>
/// — a defense-in-depth choice, not something the Meta documentation
/// explicitly requires for either check.
/// </summary>
public sealed class MetaWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    private const string SignaturePrefix = "sha256=";

    public bool IsValidVerifyToken(string? mode, string? providedToken, string configuredVerifyToken)
    {
        if (mode != "subscribe" || string.IsNullOrEmpty(providedToken) || string.IsNullOrEmpty(configuredVerifyToken))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedToken), Encoding.UTF8.GetBytes(configuredVerifyToken));
    }

    public bool IsValidSignature(ReadOnlySpan<byte> rawBody, string? signatureHeaderValue, string appSecret)
    {
        if (string.IsNullOrEmpty(appSecret))
            return false;

        if (string.IsNullOrEmpty(signatureHeaderValue) ||
            !signatureHeaderValue.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            return false;

        var providedHex = signatureHeaderValue[SignaturePrefix.Length..];
        if (providedHex.Length != 64) // SHA-256 digest is always 32 bytes = 64 hex chars.
            return false;

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> computedBytes = stackalloc byte[32];
        var appSecretBytes = Encoding.UTF8.GetBytes(appSecret);
        if (!HMACSHA256.TryHashData(appSecretBytes, rawBody, computedBytes, out var hmacBytesWritten) || hmacBytesWritten != 32)
            return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, computedBytes);
    }
}
