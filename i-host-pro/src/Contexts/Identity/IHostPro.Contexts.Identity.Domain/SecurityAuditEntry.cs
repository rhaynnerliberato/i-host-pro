using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// Append-only security audit record for the Identity &amp; Access context
/// (Incremento 2 plan, ajuste 2; Documento 07 §13/§23). Written once, in the
/// same transaction as the login/refresh/logout use case it documents (a
/// future step, not implemented yet) — exposes no mutator beyond
/// <see cref="Record"/>: an audit trail that could be edited after the fact
/// would not be a trail. Uses <see cref="Entity{TId}"/>, not
/// <see cref="AggregateRoot{TId}"/>: this record never raises further domain
/// events, it IS the recorded consequence of another aggregate's operation.
///
/// Only ever created for a tenant that has already been validly resolved —
/// an event that occurs before a tenant is known (unknown slug, inactive
/// tenant) is never persisted here (it has no tenant to be scoped/RLS
/// -protected under); such events are recorded exclusively in structured
/// logging/telemetry (Incremento 2 plan, ajuste 2).
///
/// <see cref="UserId"/>, <see cref="SessionId"/> and <see cref="RefreshTokenId"/>
/// are deliberately plain values, never foreign keys (see
/// <c>SecurityAuditEntryConfiguration</c>): this record must never be lost,
/// blocked, or cascade-deleted as a side effect of the referenced
/// User/Session/RefreshToken row being removed in the future.
///
/// <see cref="ActorId"/> (Fase 12, Checkpoint 4 — Security/Secrets/LGPD
/// Hardening) is the authenticated Administrator who PERFORMED an
/// admin-on-another-user operation — distinct from <see cref="UserId"/>,
/// which for those same operations is the TARGET user the action was done
/// to. Before this checkpoint, only the target was ever recorded, making
/// "who blocked user X" unanswerable from this table. Deliberately
/// <c>nullable</c>, with NO backfill for pre-existing rows (explicit
/// Decision Gate instruction: never invent a historical actor via
/// <c>TargetUserId</c>, a fabricated system user, <c>Guid.Empty</c>, or the
/// current caller at migration time) — a <see langword="null"/>
/// <see cref="ActorId"/> on a row written before this checkpoint means
/// "unknown," never "system." Always sourced from the already-authenticated
/// command's own <c>ActorId</c> (itself sourced exclusively from the JWT
/// claims, never the request body — see e.g. <c>BlockUserCommand</c>'s own
/// doc comment) — this class never accepts an arbitrary caller-supplied
/// value.
/// </summary>
public sealed class SecurityAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public SecurityAuditEventType EventType { get; private set; }
    public SecurityAuditReasonCode? ReasonCode { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? ActorId { get; private set; }
    public Guid? SessionId { get; private set; }
    public Guid? RefreshTokenId { get; private set; }
    public string? IpAddress { get; private set; }
    public Guid CorrelationId { get; private set; }

    private SecurityAuditEntry()
    {
        // EF Core materialization.
    }

    private SecurityAuditEntry(
        Guid id,
        Guid tenantId,
        SecurityAuditEventType eventType,
        SecurityAuditReasonCode? reasonCode,
        DateTimeOffset occurredAt,
        Guid? userId,
        Guid? actorId,
        Guid? sessionId,
        Guid? refreshTokenId,
        string? ipAddress,
        Guid correlationId)
        : base(id)
    {
        TenantId = tenantId;
        EventType = eventType;
        ReasonCode = reasonCode;
        OccurredAt = occurredAt;
        UserId = userId;
        ActorId = actorId;
        SessionId = sessionId;
        RefreshTokenId = refreshTokenId;
        IpAddress = ipAddress;
        CorrelationId = correlationId;
    }

    public static SecurityAuditEntry Record(
        Guid id,
        Guid tenantId,
        SecurityAuditEventType eventType,
        DateTimeOffset occurredAt,
        Guid correlationId,
        SecurityAuditReasonCode? reasonCode = null,
        Guid? userId = null,
        Guid? actorId = null,
        Guid? sessionId = null,
        Guid? refreshTokenId = null,
        string? ipAddress = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        if (correlationId == Guid.Empty)
            throw new ArgumentException("Correlation id cannot be empty.", nameof(correlationId));

        return new SecurityAuditEntry(
            id, tenantId, eventType, reasonCode, occurredAt, userId, actorId, sessionId, refreshTokenId, ipAddress,
            correlationId);
    }
}
