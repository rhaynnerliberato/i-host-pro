using System.Security.Cryptography;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Shared AES-256-GCM authenticated-encryption primitive, extracted out of
/// <see cref="AesGcmTokenCacheProtector"/> so a second, unrelated secret
/// (the Web OAuth PKCE code verifier — see <see cref="AesGcmOAuthTransactionSecretProtector"/>)
/// can reuse the exact same versioned payload format instead of a second,
/// duplicated crypto implementation (Web OAuth architecture gate, item 20:
/// "reuse the existing AES-256-GCM low-level primitive... no duplicate
/// cryptographic code").
///
/// Payload format (all versions must stay backward-readable):
/// <c>[1 byte version][12 bytes nonce][ciphertext][16 bytes authentication tag]</c>.
/// </summary>
internal static class AesGcmPayloadCipher
{
    private const byte CurrentVersion = 1;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    internal const int KeySizeBytes = 32; // AES-256

    internal static byte[] Protect(byte[] key, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);

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

    internal static byte[] Unprotect(byte[] key, byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(protectedData);

        if (protectedData.Length < 1 + NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Payload is too short to be valid.");

        var version = protectedData[0];
        if (version != CurrentVersion)
            throw new NotSupportedException($"Unsupported payload version {version}.");

        var ciphertextLength = protectedData.Length - 1 - NonceSizeBytes - TagSizeBytes;
        var nonce = protectedData.AsSpan(1, NonceSizeBytes);
        var ciphertext = protectedData.AsSpan(1 + NonceSizeBytes, ciphertextLength);
        var tag = protectedData.AsSpan(1 + NonceSizeBytes + ciphertextLength, TagSizeBytes);

        var plaintext = new byte[ciphertextLength];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    internal static byte[] ParseKey(string configurationKeyName, string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new InvalidOperationException(
                $"{configurationKeyName} is not configured - cannot protect/unprotect Airbnb Email Bridge secret material.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{configurationKeyName} is not valid Base64.", ex);
        }

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"{configurationKeyName} must decode to exactly {KeySizeBytes} bytes (AES-256), got {key.Length}.");
        }

        return key;
    }
}
