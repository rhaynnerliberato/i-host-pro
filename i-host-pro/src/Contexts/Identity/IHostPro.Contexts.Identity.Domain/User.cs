using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// The single source of truth for an authenticated user — not a projection of,
/// or paired with, any ASP.NET Core Identity type (Incremento 1 plan, Section
/// 2). All mutation happens through the domain methods below; every property
/// has a private setter, so Infrastructure's custom Identity stores can only
/// change state through this explicit surface, never through arbitrary
/// property assignment (Incremento 1 plan, Section 3/5).
/// </summary>
public sealed class User : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Email Email { get; private set; } = null!;

    /// <summary>
    /// Mirrors <see cref="Email"/>.NormalizedValue as a top-level, directly
    /// queryable/indexable column. Kept in sync exclusively by
    /// <see cref="ChangeEmail"/> — both values are always derived from the
    /// same normalization function (<c>EmailNormalizer</c>), so there is a
    /// single source of truth even though it is materialized in two places
    /// (EF Core cannot translate a composite index across an owner property
    /// and a member of a value-converted Value Object).
    /// </summary>
    public string NormalizedEmail { get; private set; } = null!;

    public string FullName { get; private set; } = null!;
    public PasswordHash PasswordHash { get; private set; } = null!;
    public string SecurityStamp { get; private set; } = null!;
    public UserStatus Status { get; private set; }
    public int FailedAccessCount { get; private set; }
    public DateTimeOffset? LockoutEnd { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private User()
    {
        // EF Core materialization.
    }

    private User(Guid id, Guid tenantId, Email email, string fullName, PasswordHash passwordHash, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        Email = email;
        NormalizedEmail = email.NormalizedValue;
        FullName = fullName;
        PasswordHash = passwordHash;
        SecurityStamp = NewSecurityStamp();
        Status = UserStatus.Active;
        FailedAccessCount = 0;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static User Register(
        Guid id, Guid tenantId, Email email, string fullName, PasswordHash passwordHash, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Full name cannot be empty.", nameof(fullName));

        return new User(id, tenantId, email, fullName.Trim(), passwordHash, now);
    }

    public void ChangeEmail(Email newEmail, DateTimeOffset now)
    {
        Email = newEmail;
        NormalizedEmail = newEmail.NormalizedValue;
        Touch(now);
    }

    public void ChangeFullName(string newFullName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newFullName))
            throw new ArgumentException("Full name cannot be empty.", nameof(newFullName));

        FullName = newFullName.Trim();
        Touch(now);
    }

    public void SetPasswordHash(PasswordHash newHash, DateTimeOffset now)
    {
        PasswordHash = newHash;
        SecurityStamp = NewSecurityStamp();
        Touch(now);
    }

    public void SetSecurityStamp(string stamp, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(stamp))
            throw new ArgumentException("Security stamp cannot be empty.", nameof(stamp));

        SecurityStamp = stamp;
        Touch(now);
    }

    public void IncrementFailedAccessCount(DateTimeOffset now)
    {
        FailedAccessCount++;
        Touch(now);
    }

    public void ResetFailedAccessCount(DateTimeOffset now)
    {
        FailedAccessCount = 0;
        Touch(now);
    }

    public void LockUntil(DateTimeOffset until, DateTimeOffset now)
    {
        LockoutEnd = until;
        Touch(now);
    }

    public void ClearLockout(DateTimeOffset now)
    {
        LockoutEnd = null;
        Touch(now);
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        LastLoginAt = now;
        Touch(now);
    }

    public void Block(DateTimeOffset now)
    {
        Status = UserStatus.Blocked;
        SecurityStamp = NewSecurityStamp();
        Touch(now);
    }

    public void Unblock(DateTimeOffset now)
    {
        Status = UserStatus.Active;
        Touch(now);
    }

    private static string NewSecurityStamp() => Guid.NewGuid().ToString("N");

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
