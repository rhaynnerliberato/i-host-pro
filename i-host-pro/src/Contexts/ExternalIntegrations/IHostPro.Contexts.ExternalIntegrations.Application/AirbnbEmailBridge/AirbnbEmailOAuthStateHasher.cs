using System.Security.Cryptography;
using System.Text;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// The ONE place the OAuth <c>state</c> value is hashed for storage/lookup —
/// shared between <see cref="StartAirbnbEmailWebOAuthCommandHandler"/>
/// (hashes before <see cref="IAirbnbEmailOAuthTransactionRepository.CreatePending"/>)
/// and the callback processor (hashes the value Microsoft echoes back before
/// <see cref="IAirbnbEmailOAuthTransactionRepository.ConsumeByStateHashAsync"/>).
/// Never the reverse — this is a one-way lookup key, not a token that needs
/// decrypting (Web OAuth architecture gate: "the raw state is never
/// persisted", mirrors <c>RefreshToken.TokenHash</c>'s own SHA-256 convention).
/// </summary>
public static class AirbnbEmailOAuthStateHasher
{
    public static string Hash(string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant();
}
