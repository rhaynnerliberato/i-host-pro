using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.GuestOperations.Domain;

/// <summary>
/// A single append-only audit record for who requested/performed a
/// human-or-AI-triggered action against a <see cref="GuestStayOperation"/>
/// (Fase 12, Checkpoint 4 — Security/Secrets/LGPD Hardening, Guest Access
/// Durable Audit Decision Gate). Mirrors
/// <c>Reservations.Domain.ReservationAuditEntry</c>'s own established
/// append-only pattern exactly — written in the same transaction as the
/// action it records; nothing in this Bounded Context updates or deletes a
/// row here (enforced additionally at the database level: the application
/// role has no UPDATE/DELETE grant on
/// <c>guest_operations.guest_stay_operation_audit_log</c>).
///
/// Covers ONLY the three actions that genuinely have a real human-or-AI
/// actor today: <see cref="GuestStayOperationAuditAction.AccessDeliveryRequested"/>
/// (administrator via the Api, or the AI Agent acting on the guest's own
/// explicit request), <see cref="GuestStayOperationAuditAction.CheckedIn"/>/
/// <see cref="GuestStayOperationAuditAction.CheckedOut"/> (administrator
/// only). Early-check-in/late-checkout policy APPROVAL/DENIAL is
/// deliberately never recorded here — that decision is made by this
/// platform's own deterministic policy evaluation, not a person or the AI
/// Agent, so <c>ActorType="System"</c> on those events is already correct
/// and out of scope for this table.
///
/// <see cref="ActorType"/> reuses the same closed vocabulary
/// <c>IntegrationEvent.ActorType</c> already defines platform-wide
/// ("User"/"AI"/"System"/"Integration") — never a value invented for this
/// table specifically. <see cref="ActorId"/> is always the real actor: the
/// authenticated administrator's id for "User", or the AI Agent's own
/// <c>AgentSessionId</c> for "AI" — never a fabricated human user, and never
/// a value accepted from a request body.
///
/// Deliberately carries NO guest-facing content: never an
/// AccessCredential/QR payload, message content, GuestPhone/GuestEmail, or
/// any provider payload — only audit metadata (which operation, on which
/// aggregate, by whom, when).
/// </summary>
public sealed class GuestStayOperationAuditEntry : Entity<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid GuestStayOperationId { get; private set; }
    public GuestStayOperationAuditAction Action { get; private set; }
    public string ActorType { get; private set; } = null!;
    public Guid ActorId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    private GuestStayOperationAuditEntry()
    {
        // EF Core materialization.
    }

    private GuestStayOperationAuditEntry(
        Guid id, Guid tenantId, Guid guestStayOperationId, GuestStayOperationAuditAction action,
        string actorType, Guid actorId, DateTimeOffset occurredAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        GuestStayOperationId = guestStayOperationId;
        Action = action;
        ActorType = actorType;
        ActorId = actorId;
        OccurredAtUtc = occurredAtUtc;
    }

    public static GuestStayOperationAuditEntry Record(
        Guid id, Guid tenantId, Guid guestStayOperationId, GuestStayOperationAuditAction action,
        string actorType, Guid actorId, DateTimeOffset occurredAtUtc)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));

        if (guestStayOperationId == Guid.Empty)
            throw new ArgumentException("Guest stay operation id cannot be empty.", nameof(guestStayOperationId));

        if (actorType is not ("User" or "AI"))
        {
            throw new ArgumentException(
                $"Actor type must be \"User\" or \"AI\" for the actions this table records — got \"{actorType}\".",
                nameof(actorType));
        }

        if (actorId == Guid.Empty)
            throw new ArgumentException("Actor id cannot be empty — never a fabricated/anonymous actor.", nameof(actorId));

        return new GuestStayOperationAuditEntry(id, tenantId, guestStayOperationId, action, actorType, actorId, occurredAtUtc);
    }
}

/// <summary>
/// Fixed, closed allowlist — mirrors <c>SecurityAuditEventType</c>'s own
/// pattern of a string-backed enum. Never invented beyond the three actions
/// that genuinely have a real, provable human-or-AI actor today (mandate
/// explicit instruction: never add a future action speculatively).
/// </summary>
public enum GuestStayOperationAuditAction
{
    AccessDeliveryRequested,
    CheckedIn,
    CheckedOut,
}
