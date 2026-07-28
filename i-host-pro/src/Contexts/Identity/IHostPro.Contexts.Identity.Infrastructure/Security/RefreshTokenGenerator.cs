using System.Security.Cryptography;
using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <inheritdoc cref="IRefreshTokenGenerator"/>
/// <remarks>
/// Stateless: <see cref="RandomNumberGenerator.GetBytes(int)"/> is a static,
/// thread-safe call onto the OS CSPRNG (no shared instance/state), and
/// <see cref="IOptions{TOptions}.Value"/> reads a snapshot per call — this
/// type is safe for concurrent use without any locking.
/// </remarks>
public sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private readonly IRefreshTokenHasher _hasher;
    private readonly IOptions<RefreshTokenOptions> _options;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenGenerator(IRefreshTokenHasher hasher, IOptions<RefreshTokenOptions> options, TimeProvider timeProvider)
    {
        _hasher = hasher;
        _options = options;
        _timeProvider = timeProvider;
    }

    public GeneratedRefreshToken Generate(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));

        var tokenId = Guid.NewGuid();

        var secretBytes = RandomNumberGenerator.GetBytes(_options.Value.SecretSizeBytes);
        // Base64UrlEncoder (Microsoft.IdentityModel.Tokens, already
        // referenced for JWT — no new dependency) produces the required
        // URL-safe, unpadded alphabet directly; no manual "+/=" translation
        // is needed.
        var secret = Base64UrlEncoder.Encode(secretBytes);

        // Guid.ToString("N") is always 32 lowercase hex characters, exactly
        // the canonical segment format this token requires — no additional
        // casing/formatting step needed.
        var token = $"{tenantId:N}{RefreshTokenFormat.Separator}{tokenId:N}{RefreshTokenFormat.Separator}{secret}";
        var tokenHash = _hasher.ComputeHash(token);
        var expiresAt = _timeProvider.GetUtcNow().Add(_options.Value.Lifetime);

        return new GeneratedRefreshToken(token, tokenId, tokenHash, expiresAt);
    }
}
