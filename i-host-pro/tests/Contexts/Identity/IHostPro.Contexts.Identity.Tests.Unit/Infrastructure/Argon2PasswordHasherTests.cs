using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Exercises the real Konscious-backed Argon2id primitive end-to-end — no
/// database is involved, so this genuinely runs (unlike the RLS/Postgres
/// scenarios in Tests.Integration).
/// </summary>
public class Argon2PasswordHasherTests
{
    private static readonly Argon2Options FastOptionsForTesting = new()
    {
        MemoryKib = 8 * 1024,
        Iterations = 1,
        Parallelism = 1,
        SaltSizeBytes = 16,
        HashSizeBytes = 32,
    };

    private static User CreateUser() => User.Register(
        Guid.NewGuid(), Guid.NewGuid(), Email.Create("user@ihostpro.com"), "Test User",
        PasswordHash.FromEncoded("placeholder"), DateTimeOffset.UtcNow);

    private static Argon2PasswordHasher CreateHasher(Argon2Options? options = null) =>
        new(new KonsciousArgon2idPrimitive(), Options.Create(options ?? FastOptionsForTesting));

    [Fact]
    public void HashPassword_then_VerifyHashedPassword_succeeds_for_the_correct_password()
    {
        var hasher = CreateHasher();
        var user = CreateUser();

        var hash = hasher.HashPassword(user, "correct horse battery staple");
        var result = hasher.VerifyHashedPassword(user, hash, "correct horse battery staple");

        result.Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void VerifyHashedPassword_fails_for_the_wrong_password()
    {
        var hasher = CreateHasher();
        var user = CreateUser();

        var hash = hasher.HashPassword(user, "correct horse battery staple");
        var result = hasher.VerifyHashedPassword(user, hash, "wrong password");

        result.Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void VerifyHashedPassword_fails_safely_for_a_malformed_stored_hash()
    {
        var hasher = CreateHasher();
        var user = CreateUser();

        var result = hasher.VerifyHashedPassword(user, "not-a-valid-phc-hash", "any password");

        result.Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void VerifyHashedPassword_reports_SuccessRehashNeeded_when_configured_parameters_changed()
    {
        var oldHasher = CreateHasher(FastOptionsForTesting);
        var user = CreateUser();
        var hash = oldHasher.HashPassword(user, "correct horse battery staple");

        var newOptions = new Argon2Options
        {
            MemoryKib = FastOptionsForTesting.MemoryKib * 2,
            Iterations = FastOptionsForTesting.Iterations,
            Parallelism = FastOptionsForTesting.Parallelism,
            SaltSizeBytes = FastOptionsForTesting.SaltSizeBytes,
            HashSizeBytes = FastOptionsForTesting.HashSizeBytes,
        };
        var newHasher = CreateHasher(newOptions);

        var result = newHasher.VerifyHashedPassword(user, hash, "correct horse battery staple");

        result.Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
    }
}
