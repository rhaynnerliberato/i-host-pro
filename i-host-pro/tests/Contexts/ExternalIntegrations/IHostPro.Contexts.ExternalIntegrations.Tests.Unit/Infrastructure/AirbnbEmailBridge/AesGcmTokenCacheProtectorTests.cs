using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Deterministic proof of <see cref="AesGcmTokenCacheProtector"/>'s
/// authenticated-encryption guarantees for the Airbnb Email Bridge's MSAL
/// token cache: roundtrip, nonce randomness, tamper detection, wrong-key
/// rejection, unsupported payload version, and fail-closed behavior when the
/// master key configuration is missing or malformed.
/// </summary>
public class AesGcmTokenCacheProtectorTests
{
    private const string ConfigurationKey = "ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64";

    private static AesGcmTokenCacheProtector CreateProtector(string? base64Key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(base64Key is null
                ? []
                : new Dictionary<string, string?> { [ConfigurationKey] = base64Key })
            .Build();

        return new AesGcmTokenCacheProtector(configuration);
    }

    private static string NewValidKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Protect_then_Unprotect_returns_the_original_plaintext()
    {
        var protector = CreateProtector(NewValidKeyBase64());
        var plaintext = Encoding.UTF8.GetBytes("msal-token-cache-payload");

        var protectedData = protector.Protect(plaintext);
        var roundTripped = protector.Unprotect(protectedData);

        roundTripped.Should().Equal(plaintext);
    }

    [Fact]
    public void Protect_produces_different_ciphertext_for_the_same_plaintext_each_time()
    {
        var protector = CreateProtector(NewValidKeyBase64());
        var plaintext = Encoding.UTF8.GetBytes("same-plaintext");

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        first.Should().NotEqual(second, "a random nonce must make every encryption unique, even for identical plaintext");
    }

    [Fact]
    public void Unprotect_throws_when_the_ciphertext_was_tampered_with()
    {
        var protector = CreateProtector(NewValidKeyBase64());
        var protectedData = protector.Protect(Encoding.UTF8.GetBytes("payload"));
        protectedData[^1] ^= 0xFF; // flip a bit inside the authentication tag

        var act = () => protector.Unprotect(protectedData);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_throws_when_the_key_does_not_match_the_one_used_to_protect()
    {
        var protectedData = CreateProtector(NewValidKeyBase64()).Protect(Encoding.UTF8.GetBytes("payload"));
        var wrongKeyProtector = CreateProtector(NewValidKeyBase64());

        var act = () => wrongKeyProtector.Unprotect(protectedData);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_throws_NotSupportedException_for_an_unknown_payload_version()
    {
        var protector = CreateProtector(NewValidKeyBase64());
        var protectedData = protector.Protect(Encoding.UTF8.GetBytes("payload"));
        protectedData[0] = 99;

        var act = () => protector.Unprotect(protectedData);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Protect_throws_when_the_master_key_is_not_configured()
    {
        var protector = CreateProtector(base64Key: null);

        var act = () => protector.Protect(Encoding.UTF8.GetBytes("payload"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Protect_throws_when_the_configured_key_is_not_valid_Base64()
    {
        var protector = CreateProtector("not-valid-base64!!!");

        var act = () => protector.Protect(Encoding.UTF8.GetBytes("payload"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Protect_throws_when_the_configured_key_is_the_wrong_length()
    {
        var protector = CreateProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))); // AES-128, not 256

        var act = () => protector.Protect(Encoding.UTF8.GetBytes("payload"));

        act.Should().Throw<InvalidOperationException>();
    }
}
