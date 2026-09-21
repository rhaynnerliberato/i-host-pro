using System.Security.Cryptography;
using System.Text;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// The ONE place a forgot-password token is hashed for storage/lookup —
/// shared between <see cref="IStartPasswordResetProcessor"/> (hashes before
/// <see cref="IPasswordResetTokenRepository.CreatePending"/>) and
/// <see cref="ICompletePasswordResetProcessor"/> (hashes the value the user
/// presents before <see cref="IPasswordResetTokenRepository.ConsumeByTokenHashAsync"/>).
/// Never the reverse — this is a one-way lookup key. Mirrors
/// <c>AirbnbEmailOAuthStateHasher</c>'s and <c>RefreshToken.TokenHash</c>'s
/// own SHA-256 convention: the raw token is never persisted.
/// </summary>
public static class PasswordResetTokenHasher
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
