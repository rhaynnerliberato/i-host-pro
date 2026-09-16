using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Records every call; optionally throws on publish to exercise the runner's own per-message failure isolation.</summary>
internal sealed class FakeAirbnbResolvedReservationSyncPublisher : IAirbnbResolvedReservationSyncPublisher
{
    private readonly Exception? _throwOnPublish;

    public FakeAirbnbResolvedReservationSyncPublisher(Exception? throwOnPublish = null) => _throwOnPublish = throwOnPublish;

    public List<(Guid PropertyId, string ExternalReservationId, string GuestName, DateTimeOffset CheckInAt, DateTimeOffset CheckOutAt, int GuestCount)> Calls { get; } = [];

    public Task PublishReservationImportedAsync(
        Guid propertyId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken)
    {
        Calls.Add((propertyId, externalReservationId, guestName, checkInAt, checkOutAt, guestCount));
        if (_throwOnPublish is not null)
            throw _throwOnPublish;
        return Task.CompletedTask;
    }
}
