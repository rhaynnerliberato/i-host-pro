using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Microsoft Graph delta-query cursor for one mail folder of one tenant's
/// <see cref="AirbnbEmailMailboxConnection"/>. Deliberately separate from the
/// OAuth token cache (Fase 9 review item 17) — connection/authorization state
/// and synchronization progress are independent concerns with different
/// lifecycles and different concurrency profiles.
///
/// <see cref="DeltaLink"/> is treated as an opaque Microsoft Graph value —
/// never parsed or reconstructed here, only round-tripped. A failed sync
/// attempt (<see cref="RecordFailure"/>) never advances it: a batch must be
/// safely durable before the cursor moves (Fase 9 review item 24), so a crash
/// mid-batch simply replays from the last successful cursor, protected by
/// <see cref="AirbnbEmailMessageReceipt"/>'s own per-message idempotency.
/// </summary>
public sealed class AirbnbEmailSyncState : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid MailboxConnectionId { get; private set; }
    public string MailFolderId { get; private set; } = null!;
    public string? DeltaLink { get; private set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private AirbnbEmailSyncState()
    {
        // EF Core materialization.
    }

    private AirbnbEmailSyncState(
        Guid id, Guid tenantId, Guid mailboxConnectionId, string mailFolderId, DateTimeOffset createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        MailboxConnectionId = mailboxConnectionId;
        MailFolderId = mailFolderId;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbEmailSyncState Create(
        Guid id, Guid tenantId, Guid mailboxConnectionId, string mailFolderId, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(mailFolderId))
            throw new ArgumentException("Mail folder id cannot be empty.", nameof(mailFolderId));

        return new AirbnbEmailSyncState(id, tenantId, mailboxConnectionId, mailFolderId.Trim(), createdAtUtc);
    }

    public void RecordSuccess(string? deltaLink, DateTimeOffset syncedAtUtc)
    {
        DeltaLink = deltaLink;
        LastSuccessfulSyncAtUtc = syncedAtUtc;
        LastAttemptAtUtc = syncedAtUtc;
        LastErrorCode = null;
        UpdatedAtUtc = syncedAtUtc;
    }

    public void RecordFailure(string errorCode, DateTimeOffset attemptedAtUtc)
    {
        LastAttemptAtUtc = attemptedAtUtc;
        LastErrorCode = errorCode;
        UpdatedAtUtc = attemptedAtUtc;
    }

    /// <summary>
    /// Discards the cursor entirely — required whenever the underlying
    /// mailbox identity changes (Fase 9 review item 23), since an old
    /// deltaLink must never be replayed against a different mailbox. Not
    /// invoked by anything yet; the real OAuth/reconnect flow that would call
    /// it is a later, separately authorized gate.
    /// </summary>
    public void Reset(DateTimeOffset resetAtUtc)
    {
        DeltaLink = null;
        LastSuccessfulSyncAtUtc = null;
        LastErrorCode = null;
        UpdatedAtUtc = resetAtUtc;
    }
}
