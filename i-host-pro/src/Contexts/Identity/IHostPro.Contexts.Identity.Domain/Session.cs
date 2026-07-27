using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// A login session (Documento 09 §20). Owns the lifecycle a
/// <c>RefreshToken</c> rotation chain belongs to — revoking a session revokes
/// its whole chain (Incremento 1 plan, Section 6-7).
/// </summary>
public sealed class Session : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public string? Device { get; private set; }
    public string? Browser { get; private set; }
    public string? IpAddress { get; private set; }
    public string? ApproximateLocation { get; private set; }
    public SessionStatus Status { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    private Session()
    {
        // EF Core materialization.
    }

    private Session(
        Guid id, Guid tenantId, Guid userId, DateTimeOffset now,
        string? device, string? browser, string? ipAddress)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        CreatedAt = now;
        LastActivityAt = now;
        Device = device;
        Browser = browser;
        IpAddress = ipAddress;
        Status = SessionStatus.Active;
    }

    public static Session Open(
        Guid id, Guid tenantId, Guid userId, DateTimeOffset now,
        string? device, string? browser, string? ipAddress) =>
        new(id, tenantId, userId, now, device, browser, ipAddress);

    public void Touch(DateTimeOffset now)
    {
        if (Status != SessionStatus.Active)
            throw new InvalidOperationException("Cannot touch a session that is not active.");

        LastActivityAt = now;
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (Status == SessionStatus.Revoked)
            return;

        Status = SessionStatus.Revoked;
        RevokedAt = now;
        RevocationReason = reason;
    }
}
