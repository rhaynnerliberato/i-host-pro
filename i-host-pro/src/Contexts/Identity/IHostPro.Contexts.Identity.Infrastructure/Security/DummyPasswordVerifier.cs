using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <inheritdoc cref="IDummyPasswordVerifier"/>
/// <remarks>
/// The dummy hash is computed once, lazily, and cached for the lifetime of
/// this instance (registered as a singleton) — computing a fresh dummy hash
/// per call would itself add cost beyond what a real path incurs (a real
/// login never re-hashes on every failed attempt, only verifies against an
/// already-stored hash).
///
/// <see cref="IPasswordHasher{TUser}"/> (<see cref="Argon2PasswordHasher"/>)
/// is registered Scoped, so it cannot be injected into this Singleton's
/// constructor directly — that is a captive-dependency violation that
/// <c>WebApplication.CreateBuilder</c> rejects at startup in Development
/// (<c>ValidateOnBuild</c>/<c>ValidateScopes</c>). Instead, a short-lived
/// scope is created on demand via <see cref="IServiceScopeFactory"/> — the
/// framework-sanctioned way to consume a scoped service from a singleton —
/// each time the hasher is needed. <see cref="KonsciousArgon2idPrimitive"/>
/// is stateless, so resolving a fresh instance per call has no effect on
/// correctness; it is exactly how <see cref="Argon2PasswordHasher"/> reads
/// the effectively configured <see cref="Argon2Options"/> at the moment of
/// hashing/verifying, so the dummy path's cost tracks the real, currently
/// configured parameters rather than a value fixed at process start.
///
/// <see cref="Lazy{T}"/>'s default thread-safety mode guarantees the hash
/// string itself is computed exactly once even under concurrent first use —
/// only the (cheap, stateless) hasher resolution is repeated per
/// <see cref="Verify"/> call, not the (expensive) hash generation.
///
/// Passes <see langword="null!"/> as the <c>User</c> argument to both hasher
/// calls — confirmed safe because <see cref="Argon2PasswordHasher"/> never
/// dereferences it (see <see cref="IDummyPasswordVerifier"/> for the
/// verification). If that hasher is ever changed to use its <c>User</c>
/// parameter, this class must be revisited.
/// </remarks>
public sealed class DummyPasswordVerifier : IDummyPasswordVerifier
{
    private const string DummyPassword = "ihostpro-dummy-password-for-timing-equalization";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Lazy<string> _dummyHash;

    public DummyPasswordVerifier(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _dummyHash = new Lazy<string>(ComputeDummyHash);
    }

    public void Verify(string submittedPassword)
    {
        using var scope = _scopeFactory.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        hasher.VerifyHashedPassword(null!, _dummyHash.Value, submittedPassword);
    }

    private string ComputeDummyHash()
    {
        using var scope = _scopeFactory.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        return hasher.HashPassword(null!, DummyPassword);
    }
}
