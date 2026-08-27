namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Supports the cardinality rule for Late Checkout requests (Fase 10,
/// Checkpoint 3 mandate): at most one active request may exist per
/// Reservation at a time, where <c>PendingPayment</c> counts as active
/// alongside <c>Pending</c> — the one difference from
/// <see cref="IEarlyCheckInRequestReader"/>'s own rule.
/// </summary>
public interface ILateCheckoutRequestReader
{
    /// <summary>True when a <c>Pending</c> or <c>PendingPayment</c> request already exists for <paramref name="reservationId"/>.</summary>
    Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken);
}
