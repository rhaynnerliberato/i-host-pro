using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeReservationReferenceProjection : IReservationReferenceProjection
{
    private readonly bool _exists;
    private readonly bool _isCancelled;

    private FakeReservationReferenceProjection(bool exists, bool isCancelled)
    {
        _exists = exists;
        _isCancelled = isCancelled;
    }

    public static FakeReservationReferenceProjection With(bool exists) => new(exists, isCancelled: false);

    public static FakeReservationReferenceProjection With(bool exists, bool isCancelled) => new(exists, isCancelled);

    public Task<bool> ExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_exists);

    public Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_isCancelled);
}
