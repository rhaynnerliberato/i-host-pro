using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// One link of a session's refresh-token rotation chain (Incremento 1 plan,
/// Section 6-7). <see cref="TokenId"/> is the public, indexed selector embedded
/// in the opaque token string handed to the client; <see cref="TokenHash"/> is
/// the SHA-256 hash of the entire presented token string (never just the
/// secret portion) — the Domain never sees or stores a plaintext secret.
/// </summary>
public sealed class RefreshToken : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TokenId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public RefreshTokenRevocationReason? RevocationReason { get; private set; }
    public Guid? PreviousTokenId { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    private RefreshToken()
    {
        // EF Core materialization.
    }

    private RefreshToken(
        Guid id, Guid tokenId, Guid tenantId, Guid sessionId, Guid userId,
        string tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, Guid? previousTokenId)
        : base(id)
    {
        TokenId = tokenId;
        TenantId = tenantId;
        SessionId = sessionId;
        UserId = userId;
        TokenHash = tokenHash;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        PreviousTokenId = previousTokenId;
    }

    public static RefreshToken Issue(
        Guid id, Guid tokenId, Guid tenantId, Guid sessionId, Guid userId,
        string tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, Guid? previousTokenId = null)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new ArgumentException("Token hash cannot be empty.", nameof(tokenHash));
        if (expiresAt <= issuedAt)
            throw new ArgumentException("Expiration must be after issuance.", nameof(expiresAt));

        return new RefreshToken(id, tokenId, tenantId, sessionId, userId, tokenHash, issuedAt, expiresAt, previousTokenId);
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Marks this token as rotated (consumed by a refresh) and links its
    /// successor. Throws if this token was already revoked — an already-revoked
    /// token being presented again is reuse, a distinct, security-relevant case
    /// the caller must handle separately, never as an ordinary rotation
    /// (Incremento 1 plan, Section 7).
    /// </summary>
    public void MarkRotated(Guid replacedByTokenId, DateTimeOffset now)
    {
        if (IsRevoked)
            throw new InvalidOperationException("Cannot rotate a refresh token that has already been revoked.");

        RevokedAt = now;
        RevocationReason = RefreshTokenRevocationReason.Rotated;
        ReplacedByTokenId = replacedByTokenId;
    }

    public void Revoke(RefreshTokenRevocationReason reason, DateTimeOffset now)
    {
        if (IsRevoked)
            return;

        RevokedAt = now;
        RevocationReason = reason;
    }
}
