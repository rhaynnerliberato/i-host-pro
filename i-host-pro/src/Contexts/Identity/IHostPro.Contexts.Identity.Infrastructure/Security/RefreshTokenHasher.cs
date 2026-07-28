using System.Security.Cryptography;
using System.Text;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <inheritdoc cref="IRefreshTokenHasher"/>
/// <remarks>
/// Stateless — every member reads only its parameters, so this type is
/// trivially safe for concurrent use. <see cref="SHA256.HashData(byte[])"/>
/// and <see cref="CryptographicOperations.FixedTimeEquals"/> are themselves
/// safe for concurrent invocation (no shared mutable state involved).
/// </remarks>
public sealed class RefreshTokenHasher : IRefreshTokenHasher
{
    public string ComputeHash(string presentedToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedToken);

        var bytes = Encoding.UTF8.GetBytes(presentedToken);
        var hash = SHA256.HashData(bytes);

        // 64 lowercase hex characters — matches identity.refresh_tokens.
        // token_hash exactly (character varying(64), confirmed against
        // RefreshTokenConfiguration before implementing this class).
        return Convert.ToHexStringLower(hash);
    }

    public bool Verify(string presentedToken, string expectedHashHex)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedToken);
        ArgumentException.ThrowIfNullOrEmpty(expectedHashHex);

        var actualHashHex = ComputeHash(presentedToken);

        // Never a plain string/sequence equality check: two hex strings
        // compared with == or SequenceEqual short-circuit on the first
        // differing character, which is a timing side channel. Comparing
        // the UTF-8 bytes of both hex strings is equivalent in strength to
        // comparing the raw 32-byte digests — the full 256 bits of entropy
        // are still represented, just as 64 ASCII bytes instead of 32 raw
        // ones — while avoiding a separate hex-decode step.
        //
        // FixedTimeEquals itself defines a length mismatch as an immediate,
        // safe "not equal" (length is not a secret) — no separate length
        // check is needed here.
        var actualBytes = Encoding.UTF8.GetBytes(actualHashHex);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHashHex);

        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
