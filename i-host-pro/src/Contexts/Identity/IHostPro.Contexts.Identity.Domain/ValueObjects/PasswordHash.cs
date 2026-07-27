using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain.ValueObjects;

/// <summary>
/// Opaque wrapper around an encoded password hash string (PHC format). The
/// Domain never interprets its contents — algorithm, memory/iteration/
/// parallelism parameters and encoding all belong to Infrastructure
/// (<c>Argon2PasswordHasher</c> / <c>Argon2PhcParser</c>). Password policy
/// (minimum length, complexity) is likewise not enforced here — it is
/// configuration-driven and belongs to Infrastructure/Application
/// (Incremento 1 plan, "Política de senha").
/// </summary>
public sealed class PasswordHash : ValueObject
{
    public string Value { get; }

    private PasswordHash(string value) => Value = value;

    public static PasswordHash FromEncoded(string encodedHash)
    {
        if (string.IsNullOrWhiteSpace(encodedHash))
            throw new ArgumentException("Password hash cannot be empty.", nameof(encodedHash));

        return new PasswordHash(encodedHash);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }
}
