using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeReservationCancellationGuard : IReservationCancellationGuard
{
    public List<(Guid TenantId, Guid ReservationId)> AcquiredLocks { get; } = [];

    public Task AcquireLockAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        AcquiredLocks.Add((tenantId, reservationId));
        return Task.CompletedTask;
    }
}
