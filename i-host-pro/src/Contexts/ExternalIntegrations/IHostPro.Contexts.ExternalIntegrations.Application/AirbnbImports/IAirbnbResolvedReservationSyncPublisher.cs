namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;

/// <summary>
/// Publishes an Airbnb reservation import event when the <c>PropertyId</c>
/// has ALREADY been resolved upstream (e.g. the Airbnb Email Bridge's own
/// <c>AirbnbListingTitleMapping</c> — a free-text listing-title resolution,
/// never the stable Airbnb listing id <see cref="IAirbnbReservationSyncPublisher"/>
/// requires). Deliberately a SEPARATE interface rather than an overload on
/// <see cref="IAirbnbReservationSyncPublisher"/>: that interface's whole
/// contract is "resolve ExternalListingId → PropertyId via
/// AirbnbListingMapping" (CP3.2 mandate) — overloading it with a
/// caller-already-resolved-PropertyId path would blur that responsibility
/// into a confusing catch-all. Both interfaces publish the exact same
/// <see cref="Contracts.AirbnbReservationImported"/> event and converge on
/// the SAME downstream <c>AirbnbReservationImportedProcessor</c> /
/// <c>Reservation.CreateImported</c> path — no new event type, no
/// duplicated domain/idempotency logic. Never fails: unlike the
/// listing-id-based publisher, there is no mapping lookup that can come up
/// empty here — the caller already proved the PropertyId exists.
/// </summary>
public interface IAirbnbResolvedReservationSyncPublisher
{
    Task PublishReservationImportedAsync(
        Guid propertyId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken);
}
