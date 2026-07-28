namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Hashes/verifies a refresh token exactly as presented (Incremento 2 plan,
/// Etapa 7) — the hash covers the entire presented string, never just a
/// segment of it. Never logs, echoes, or includes the token or the hash in
/// any exception it raises.
/// </summary>
public interface IRefreshTokenHasher
{
    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="presentedToken"/>, as 64 lowercase hex characters.</summary>
    string ComputeHash(string presentedToken);

    /// <summary>
    /// True when <paramref name="presentedToken"/> hashes to exactly
    /// <paramref name="expectedHashHex"/>. Compares in fixed time — never a
    /// plain string/sequence equality check.
    /// </summary>
    bool Verify(string presentedToken, string expectedHashHex);
}
