using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeReservationReferenceProjection : IReservationReferenceProjection
{
    private readonly bool _exists;
    private bool _isCancelled;

    private FakeReservationReferenceProjection(bool exists, bool isCancelled)
    {
        _exists = exists;
        _isCancelled = isCancelled;
    }

    public static FakeReservationReferenceProjection With(bool exists) => new(exists, isCancelled: false);

    public static FakeReservationReferenceProjection With(bool exists, bool isCancelled) => new(exists, isCancelled);

    public bool EnsureExistsCalled { get; private set; }

    public Task<bool> ExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_exists);

    public Task EnsureExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        EnsureExistsCalled = true;
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_isCancelled);
}
