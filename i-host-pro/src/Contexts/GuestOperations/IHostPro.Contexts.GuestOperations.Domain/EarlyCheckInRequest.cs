using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Domain;

/// <summary>
/// A guest's request to check in earlier than the Reservation's current
/// <c>CheckInAt</c> (Fase 10, Checkpoint 3). Evaluation is synchronous and
/// automatic — there is no manual-approval step (Documento 10's own flow):
/// the deciding command handler creates this row already <see cref="EarlyCheckInRequestStatus.Pending"/>
/// and transitions it to <see cref="Approve"/>/<see cref="Deny"/> in the SAME
/// unit of work, before the transaction commits. Deliberately minimal — no
/// guest-facing form data, no notification-delivery state: those are policy
/// characteristics evaluated at decision time, not facts this request needs
/// to persist.
///
/// <see cref="ReservationId"/>/<see cref="PropertyId"/> carry no physical
/// foreign key (mirrors <see cref="GuestStayOperation"/>'s own opaque-Guid
/// convention). At most one <see cref="EarlyCheckInRequestStatus.Pending"/>
/// request may exist per Reservation at a time — enforced by a partial
/// unique index in Infrastructure, never here.
/// </summary>
public sealed class EarlyCheckInRequest : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public DateTimeOffset RequestedCheckInAt { get; private set; }
    public EarlyCheckInRequestStatus Status { get; private set; }
    public EarlyCheckInDenialReason? DenialReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private EarlyCheckInRequest()
    {
        // EF Core materialization.
    }

    private EarlyCheckInRequest(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId,
        DateTimeOffset requestedCheckInAt, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        PropertyId = propertyId;
        RequestedCheckInAt = requestedCheckInAt;
        Status = EarlyCheckInRequestStatus.Pending;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static EarlyCheckInRequest Create(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId,
        DateTimeOffset requestedCheckInAt, DateTimeOffset now)
    {
        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation id cannot be empty.", nameof(reservationId));

        if (propertyId == Guid.Empty)
            throw new ArgumentException("Property id cannot be empty.", nameof(propertyId));

        return new EarlyCheckInRequest(id, tenantId, reservationId, propertyId, requestedCheckInAt, now);
    }

    /// <summary>
    /// <see cref="EarlyCheckInRequestStatus.Pending"/> → <see cref="EarlyCheckInRequestStatus.Approved"/>.
    /// Terminal — no restoration exists. The caller is responsible for having
    /// already run every precondition/policy/schedule/cleaning-readiness
    /// check BEFORE calling this; this guard is defense-in-depth only.
    /// </summary>
    public void Approve(DateTimeOffset now)
    {
        if (Status != EarlyCheckInRequestStatus.Pending)
            throw new InvalidOperationException($"Cannot approve an early check-in request in status '{Status}'.");

        Status = EarlyCheckInRequestStatus.Approved;
        DecidedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// <see cref="EarlyCheckInRequestStatus.Pending"/> → <see cref="EarlyCheckInRequestStatus.Denied"/>.
    /// Terminal — no restoration exists. <paramref name="reason"/> must be a
    /// known negative business decision — never used for an infrastructure
    /// failure or a missing precondition, which are surfaced as a failed
    /// command result before a request row is ever created.
    /// </summary>
    public void Deny(EarlyCheckInDenialReason reason, DateTimeOffset now)
    {
        if (Status != EarlyCheckInRequestStatus.Pending)
            throw new InvalidOperationException($"Cannot deny an early check-in request in status '{Status}'.");

        Status = EarlyCheckInRequestStatus.Denied;
        DenialReason = reason;
        DecidedAtUtc = now;
        UpdatedAtUtc = now;
    }
}
