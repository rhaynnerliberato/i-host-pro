using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Reads the current payment status for a Reservation (Fase 11, Checkpoint 3
/// — AI Agent's own <c>GetPaymentStatus</c> Read Tool, Exception #3).
/// <see cref="ReservationId"/> is always backend-derived by the caller, never
/// model-supplied.
///
/// A Reservation may accumulate more than one <c>PixCharge</c> over time
/// (e.g. a failed/expired charge followed by a fresh one). The tie-break is
/// the mandate's own explicit, approved rule (Fase 11, Checkpoint 3 mandate
/// item 16): most recent by <c>CreatedAtUtc DESC</c>, then <c>Id DESC</c> —
/// never "most Confirmed"/"best status"/"active first".
/// </summary>
public sealed record GetPaymentStatusByReservationQuery(Guid ReservationId) : IQuery<PaymentStatusResult>;
