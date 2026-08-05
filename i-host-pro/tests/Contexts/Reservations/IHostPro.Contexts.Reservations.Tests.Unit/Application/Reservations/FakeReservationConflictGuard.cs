using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

internal sealed class FakeReservationConflictGuard : IReservationConflictGuard
{
    private readonly bool _hasConflict;

    private FakeReservationConflictGuard(bool hasConflict) => _hasConflict = hasConflict;

    public static FakeReservationConflictGuard WithConflict(bool hasConflict) => new(hasConflict);

    public int AcquirePropertyLockAsyncCallCount { get; private set; }
    public int HasConflictingReservationAsyncCallCount { get; private set; }

    public Task AcquirePropertyLockAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        AcquirePropertyLockAsyncCallCount++;
        return Task.CompletedTask;
    }

    public Task<bool> HasConflictingReservationAsync(
        Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
        Guid? excludeReservationId, CancellationToken cancellationToken)
    {
        HasConflictingReservationAsyncCallCount++;
        return Task.FromResult(_hasConflict);
    }
}
