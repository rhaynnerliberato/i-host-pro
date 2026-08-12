using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeReservationReferenceProjection : IReservationReferenceProjection
{
    private readonly bool _exists;

    private FakeReservationReferenceProjection(bool exists) => _exists = exists;

    public static FakeReservationReferenceProjection With(bool exists) => new(exists);

    public Task<bool> ExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_exists);
}
