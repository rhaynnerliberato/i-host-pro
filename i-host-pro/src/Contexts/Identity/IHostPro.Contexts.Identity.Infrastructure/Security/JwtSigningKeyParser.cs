using System.Security.Cryptography;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Single source of truth for parsing and validating the RSA JWT signing key
/// (ADR-012, Incremento 2 plan, Etapa 6) — used both by
/// <see cref="JwtSigningKeyOptionsValidator"/> (fails host startup fast, via
/// <c>ValidateOnStart</c>) and by <see cref="ConfigurationJwtSigningKeyProvider"/>
/// itself (defensive check when constructed outside the validated DI
/// pipeline, e.g. directly in a unit test).
///
/// Never includes the PEM content, RSA parameters, or any derived key
/// material in an exception message or anywhere else — only the fact that
/// validation failed and why, in general terms.
/// </summary>
public static class JwtSigningKeyParser
{
    public const int MinimumKeySizeBits = 2048;

    public static RSA ParseAndValidate(string? privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException(
                "The JWT signing private key is missing. Provide it via an environment variable or User Secrets " +
                "— never in a committed appsettings file.");
        }

        RSA rsa;
        try
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                "The JWT signing private key is not a valid PEM-encoded RSA private key.", ex);
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            var actualSize = rsa.KeySize;
            rsa.Dispose();
            throw new InvalidOperationException(
                $"The JWT signing key must be at least {MinimumKeySizeBits} bits (was {actualSize}).");
        }

        return rsa;
    }
}
