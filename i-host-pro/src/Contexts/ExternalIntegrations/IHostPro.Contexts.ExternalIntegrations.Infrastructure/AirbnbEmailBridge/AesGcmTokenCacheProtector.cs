using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// AES-256-GCM implementation of <see cref="ITokenCacheProtector"/>. The
/// master key is resolved lazily, on first use, from
/// <c>ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64</c>
/// (via <see cref="IConfiguration"/> — .NET User Secrets or an environment
/// variable in Development, never committed) — deliberately NOT validated at
/// host startup, since the Airbnb Email Bridge is an optional feature no
/// tenant has connected yet; a missing key must not block Api/Worker startup
/// for every other developer (Fase 9 review item: "Do NOT implement cloud
/// storage now" / key source swap is a future, separate decision).
///
/// Payload format (all versions must stay backward-readable):
/// <c>[1 byte version][12 bytes nonce][ciphertext][16 bytes authentication tag]</c>.
/// If the master key is ever rotated without a re-encryption migration,
/// existing payloads simply fail to decrypt (<see cref="CryptographicException"/>)
/// — this is intentional fail-closed behavior, not corruption: the affected
/// mailbox must be reconnected.
/// </summary>
public sealed class AesGcmTokenCacheProtector : ITokenCacheProtector
{
    private const string KeyConfigurationKey = "ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64";
    private const byte CurrentVersion = 1;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeySizeBytes = 32; // AES-256

    private readonly IConfiguration _configuration;

    public AesGcmTokenCacheProtector(IConfiguration configuration) => _configuration = configuration;

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = ResolveKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new byte[1 + NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        payload[0] = CurrentVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSizeBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSizeBytes + ciphertext.Length, TagSizeBytes);
        return payload;
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);

        if (protectedData.Length < 1 + NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Token cache payload is too short to be valid.");

        var version = protectedData[0];
        if (version != CurrentVersion)
            throw new NotSupportedException($"Unsupported token cache payload version {version}.");

        var ciphertextLength = protectedData.Length - 1 - NonceSizeBytes - TagSizeBytes;
        var nonce = protectedData.AsSpan(1, NonceSizeBytes);
        var ciphertext = protectedData.AsSpan(1 + NonceSizeBytes, ciphertextLength);
        var tag = protectedData.AsSpan(1 + NonceSizeBytes + ciphertextLength, TagSizeBytes);

        var key = ResolveKey();
        var plaintext = new byte[ciphertextLength];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private byte[] ResolveKey()
    {
        var base64Key = _configuration[KeyConfigurationKey];
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException(
                $"{KeyConfigurationKey} is not configured - cannot protect/unprotect the Airbnb Email Bridge token cache.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{KeyConfigurationKey} is not valid Base64.", ex);
        }

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"{KeyConfigurationKey} must decode to exactly {KeySizeBytes} bytes (AES-256), got {key.Length}.");
        }

        return key;
    }
}
