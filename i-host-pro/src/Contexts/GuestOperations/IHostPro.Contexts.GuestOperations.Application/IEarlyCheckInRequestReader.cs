namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Supports the cardinality rule for Early Check-in requests (Fase 10,
/// Checkpoint 3 mandate): at most one <c>Pending</c> request may exist per
/// Reservation at a time. Mirrors <see cref="IGuestStayOperationReader"/>'s
/// own minimal, reservation-scoped shape.
/// </summary>
public interface IEarlyCheckInRequestReader
{
    /// <summary>True when a <c>Pending</c> request already exists for <paramref name="reservationId"/>.</summary>
    Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken);
}
