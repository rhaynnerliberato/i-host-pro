using FluentAssertions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Confirms <see cref="DummyPasswordVerifier"/> uses the effectively
/// configured <see cref="Argon2Options"/> at runtime rather than a value
/// fixed in code, and that the dummy hash itself is generated exactly once,
/// never per attempt (Incremento 2 plan, Etapa 9 -&gt; 10 pendência 2).
///
/// <see cref="DummyPasswordVerifier"/> creates a fresh
/// <see cref="IServiceScopeFactory"/> scope per call (the sanctioned way to
/// consume the Scoped <see cref="IPasswordHasher{TUser}"/> from a Singleton),
/// so a decorator that reports into a shared, closure-captured counter is
/// used instead of asserting on a specific hasher instance — a new
/// <c>IPasswordHasher&lt;User&gt;</c> instance exists per scope, but they all
/// report into the same counter.
/// </summary>
public class DummyPasswordVerifierTests
{
    private sealed class HashCallRecordingDecorator : IPasswordHasher<User>
    {
        private readonly IPasswordHasher<User> _inner;
        private readonly Action<string> _onHash;

        public HashCallRecordingDecorator(IPasswordHasher<User> inner, Action<string> onHash)
        {
            _inner = inner;
            _onHash = onHash;
        }

        public string HashPassword(User user, string password)
        {
            var hash = _inner.HashPassword(user, password);
            _onHash(hash);
            return hash;
        }

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword) =>
            _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    private static ServiceProvider BuildContainer(Action<Argon2Options> configureArgon2, Action<string> onHashGenerated)
    {
        var services = new ServiceCollection();
        services.Configure(configureArgon2);
        services.AddScoped<IArgon2idPrimitive, KonsciousArgon2idPrimitive>();
        services.AddScoped<Argon2PasswordHasher>();
        services.AddScoped<IPasswordHasher<User>>(sp =>
            new HashCallRecordingDecorator(sp.GetRequiredService<Argon2PasswordHasher>(), onHashGenerated));

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Verify_does_not_throw_and_discards_the_result()
    {
        using var provider = BuildContainer(o => { o.MemoryKib = 8; o.Iterations = 1; }, _ => { });
        var verifier = new DummyPasswordVerifier(provider.GetRequiredService<IServiceScopeFactory>());

        var act = () => verifier.Verify("whatever-the-caller-submitted");

        act.Should().NotThrow();
    }

    [Fact]
    public void The_dummy_hash_is_generated_exactly_once_across_many_Verify_calls()
    {
        // A single closure-captured counter survives across the distinct
        // scopes DummyPasswordVerifier creates internally (a fresh
        // IPasswordHasher<User> per scope, but each reports into the same
        // counter) — proving HashPassword itself (the expensive part being
        // cached) runs only once overall, no matter how many attempts call
        // Verify.
        var callCount = 0;
        using var provider = BuildContainer(o => { o.MemoryKib = 8; o.Iterations = 1; }, _ => callCount++);
        var verifier = new DummyPasswordVerifier(provider.GetRequiredService<IServiceScopeFactory>());

        verifier.Verify("attempt-one");
        verifier.Verify("attempt-two");
        verifier.Verify("attempt-three");

        callCount.Should().Be(1);
    }

    [Fact]
    public void The_dummy_hash_reflects_the_currently_configured_Argon2_parameters_not_a_fixed_default()
    {
        // Deliberately different from Argon2Options' own defaults (MemoryKib
        // 19_456, Iterations 2) to prove the dummy hash tracks whatever is
        // actually configured, rather than a value fixed in code.
        var callCount = 0;
        string? lastHash = null;
        using var provider = BuildContainer(
            o => { o.MemoryKib = 8; o.Iterations = 3; o.Parallelism = 1; o.SaltSizeBytes = 16; o.HashSizeBytes = 16; },
            hash => { callCount++; lastHash = hash; });
        var verifier = new DummyPasswordVerifier(provider.GetRequiredService<IServiceScopeFactory>());

        verifier.Verify("whatever-the-caller-submitted");

        callCount.Should().Be(1);
        Argon2PhcParser.TryParse(lastHash, out var parsed).Should().BeTrue();
        parsed!.MemoryKib.Should().Be(8);
        parsed.Iterations.Should().Be(3);
        parsed.Parallelism.Should().Be(1);
    }
}
