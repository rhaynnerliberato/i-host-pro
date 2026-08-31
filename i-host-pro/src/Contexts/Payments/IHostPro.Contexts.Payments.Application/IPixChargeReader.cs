namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Supports the cardinality rule for PIX charges (Fase 10, Checkpoint 5
/// mandate item 14): at most one ACTIVE (<c>Pending</c>) charge may exist
/// per <c>LateCheckoutRequestId</c> at a time. Also backs the idempotency
/// guard for <c>LateCheckoutPaymentRequiredChargeInitializer</c> — a
/// redelivered <c>LateCheckoutPaymentRequired</c> must never create a
/// second charge (mandate item 15).
/// </summary>
public interface IPixChargeReader
{
    /// <summary>The id of the active (<c>Pending</c>) charge for <paramref name="lateCheckoutRequestId"/>, or <see langword="null"/> if none exists.</summary>
    Task<Guid?> GetActiveIdByLateCheckoutRequestIdAsync(Guid lateCheckoutRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Minimal status projection for a Reservation (Fase 11, Checkpoint 3 —
    /// AI Agent's own <c>GetPaymentStatus</c> Read Tool). When more than one
    /// <c>PixCharge</c> exists for <paramref name="reservationId"/>, picks
    /// the most recent by <c>CreatedAtUtc DESC</c>, then <c>Id DESC</c> as a
    /// deterministic tie-breaker (mandate item 16) — never a status-based
    /// priority. <see langword="null"/> when no <c>PixCharge</c> exists yet
    /// for this Reservation.
    /// </summary>
    Task<PaymentStatusResult?> GetStatusByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken);
}
