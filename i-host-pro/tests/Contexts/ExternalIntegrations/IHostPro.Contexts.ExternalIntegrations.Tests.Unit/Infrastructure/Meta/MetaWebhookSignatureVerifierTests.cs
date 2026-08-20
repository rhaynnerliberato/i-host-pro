using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.Meta;

/// <summary>
/// Fase 9, Checkpoint 2.3.1 mandate §23-25: deterministic proof of the two
/// Meta webhook security surfaces — GET verify-token comparison and POST
/// <c>X-Hub-Signature-256</c> HMAC-SHA256 verification, including the exact-
/// bytes requirement (a whitespace-only change to an otherwise-equivalent
/// JSON body must invalidate a signature computed for the original bytes).
/// </summary>
public class MetaWebhookSignatureVerifierTests
{
    private readonly MetaWebhookSignatureVerifier _verifier = new();

    // ---- GET verify-token handshake --------------------------------------

    [Fact]
    public void IsValidVerifyToken_accepts_subscribe_mode_with_the_matching_token()
    {
        _verifier.IsValidVerifyToken("subscribe", "correct-token", "correct-token").Should().BeTrue();
    }

    [Fact]
    public void IsValidVerifyToken_rejects_a_wrong_token()
    {
        _verifier.IsValidVerifyToken("subscribe", "wrong-token", "correct-token").Should().BeFalse();
    }

    [Fact]
    public void IsValidVerifyToken_rejects_a_missing_token()
    {
        _verifier.IsValidVerifyToken("subscribe", null, "correct-token").Should().BeFalse();
    }

    [Fact]
    public void IsValidVerifyToken_rejects_a_wrong_mode()
    {
        _verifier.IsValidVerifyToken("unsubscribe", "correct-token", "correct-token").Should().BeFalse();
    }

    [Fact]
    public void IsValidVerifyToken_rejects_a_missing_mode()
    {
        _verifier.IsValidVerifyToken(null, "correct-token", "correct-token").Should().BeFalse();
    }

    // ---- POST X-Hub-Signature-256 ------------------------------------------

    [Fact]
    public void IsValidSignature_accepts_a_correctly_signed_body()
    {
        const string appSecret = "app-secret-value";
        var body = "{\"object\":\"whatsapp_business_account\"}"u8.ToArray();
        var header = ComputeValidHeader(body, appSecret);

        _verifier.IsValidSignature(body, header, appSecret).Should().BeTrue();
    }

    [Fact]
    public void IsValidSignature_rejects_a_missing_header()
    {
        var body = "{}"u8.ToArray();

        _verifier.IsValidSignature(body, null, "app-secret-value").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_an_empty_header()
    {
        var body = "{}"u8.ToArray();

        _verifier.IsValidSignature(body, string.Empty, "app-secret-value").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_a_header_with_the_wrong_prefix()
    {
        const string appSecret = "app-secret-value";
        var body = "{}"u8.ToArray();
        var correctHeader = ComputeValidHeader(body, appSecret);
        var wrongPrefixHeader = "sha1=" + correctHeader[7..];

        _verifier.IsValidSignature(body, wrongPrefixHeader, appSecret).Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_malformed_hex()
    {
        var body = "{}"u8.ToArray();

        _verifier.IsValidSignature(body, "sha256=not-valid-hex-zzzz", "app-secret-value").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_hex_of_the_wrong_length()
    {
        var body = "{}"u8.ToArray();

        _verifier.IsValidSignature(body, "sha256=abcd", "app-secret-value").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_an_incorrect_signature()
    {
        var body = "{}"u8.ToArray();
        var wrongSignature = "sha256=" + new string('0', 64);

        _verifier.IsValidSignature(body, wrongSignature, "app-secret-value").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_a_body_tampered_after_signing()
    {
        const string appSecret = "app-secret-value";
        var originalBody = "{\"status\":\"sent\"}"u8.ToArray();
        var header = ComputeValidHeader(originalBody, appSecret);
        var tamperedBody = "{\"status\":\"read\"}"u8.ToArray();

        _verifier.IsValidSignature(tamperedBody, header, appSecret).Should().BeFalse();
    }

    /// <summary>
    /// Exact-bytes proof (mandate §25): two JSON payloads that are
    /// semantically equivalent but differ only in whitespace must NOT share a
    /// valid signature — proves the verifier hashes the raw bytes received,
    /// never a re-serialized/normalized form.
    /// </summary>
    [Fact]
    public void IsValidSignature_rejects_a_semantically_equivalent_body_with_different_whitespace()
    {
        const string appSecret = "app-secret-value";
        var compactBody = "{\"a\":1}"u8.ToArray();
        var spacedBody = "{ \"a\": 1 }"u8.ToArray();
        var headerForCompactBody = ComputeValidHeader(compactBody, appSecret);

        _verifier.IsValidSignature(spacedBody, headerForCompactBody, appSecret).Should().BeFalse(
            "the signature was computed over the compact body's exact bytes — a whitespace-only variant must not validate against it");
    }

    [Fact]
    public void IsValidSignature_rejects_a_single_byte_change()
    {
        const string appSecret = "app-secret-value";
        var body = "{\"n\":1}"u8.ToArray();
        var header = ComputeValidHeader(body, appSecret);
        var mutated = (byte[])body.Clone();
        mutated[^2] = (byte)'2'; // "1" -> "2"

        _verifier.IsValidSignature(mutated, header, appSecret).Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_when_the_app_secret_is_wrong()
    {
        var body = "{}"u8.ToArray();
        var header = ComputeValidHeader(body, "the-real-secret");

        _verifier.IsValidSignature(body, header, "a-different-secret").Should().BeFalse();
    }

    [Fact]
    public void IsValidSignature_rejects_when_the_app_secret_is_empty()
    {
        var body = "{}"u8.ToArray();
        var header = ComputeValidHeader(body, "the-real-secret");

        _verifier.IsValidSignature(body, header, string.Empty).Should().BeFalse();
    }

    private static string ComputeValidHeader(byte[] body, string appSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
