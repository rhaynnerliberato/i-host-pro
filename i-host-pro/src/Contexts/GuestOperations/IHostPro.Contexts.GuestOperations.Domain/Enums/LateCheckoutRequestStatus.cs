namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// A Late Checkout request's lifecycle (Fase 10, Checkpoint 3). Mirrors
/// <see cref="EarlyCheckInRequestStatus"/> exactly, plus
/// <see cref="PendingPayment"/> — the payment boundary this checkpoint
/// deliberately stops at (ADR-024 amendment): when the resolved
/// <c>LateCheckoutPolicy.RequiresPix</c> is true and a charge is
/// determinable, the request settles here, NEVER <see cref="Approved"/>,
/// until Fase 10 Checkpoint 5 (PIX Payment Boundary) produces a real
/// confirmation. Unlike every other non-<see cref="Pending"/> value,
/// <see cref="PendingPayment"/> is NOT terminal — it is the one state a
/// future checkpoint is expected to transition onward from. <see cref="Paid"/>/
/// <see cref="Failed"/>/<see cref="Expired"/> deliberately do not exist here
/// — those belong to a Payment's own lifecycle (Checkpoint 5), never this
/// request's.
/// </summary>
public enum LateCheckoutRequestStatus
{
    Pending = 0,
    PendingPayment = 1,
    Approved = 2,
    Denied = 3,
    Cancelled = 4,
}
