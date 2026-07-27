using System.Security.Cryptography;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Custom Argon2id <see cref="IPasswordHasher{TUser}"/>, replacing the
/// PBKDF2-based default (ADR-005). Compares hashes with a fixed-time
/// comparison and reports <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>
/// whenever the stored hash's parameters differ from the currently configured
/// ones, enabling transparent rehash-on-login (Incremento 1 plan, Section 13).
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher<User>
{
    private readonly IArgon2idPrimitive _primitive;
    private readonly Argon2Options _options;

    public Argon2PasswordHasher(IArgon2idPrimitive primitive, IOptions<Argon2Options> options)
    {
        _primitive = primitive;
        _options = options.Value;
    }

    public string HashPassword(User user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(_options.SaltSizeBytes);
        var hash = _primitive.Hash(
            password, salt, _options.MemoryKib, _options.Iterations, _options.Parallelism, _options.HashSizeBytes);

        return new Argon2PhcHash(_options.MemoryKib, _options.Iterations, _options.Parallelism, salt, hash).Encode();
    }

    public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        if (!Argon2PhcParser.TryParse(hashedPassword, out var parsed) || parsed is null)
            return PasswordVerificationResult.Failed;

        var computed = _primitive.Hash(
            providedPassword, parsed.Salt, parsed.MemoryKib, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);

        if (!CryptographicOperations.FixedTimeEquals(computed, parsed.Hash))
            return PasswordVerificationResult.Failed;

        var needsRehash =
            parsed.MemoryKib != _options.MemoryKib ||
            parsed.Iterations != _options.Iterations ||
            parsed.Parallelism != _options.Parallelism ||
            parsed.Hash.Length != _options.HashSizeBytes;

        return needsRehash ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success;
    }
}
