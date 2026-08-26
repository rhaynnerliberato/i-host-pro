using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;

/// <summary>
/// Resolves <c>ExternalListingId → PropertyId</c> (via <c>AirbnbListingMapping</c>,
/// CP3.2 mandate §3) and publishes the resulting Airbnb reservation event
/// through this context's own durable outbox (Fase 9, Checkpoint 3.2 —
/// "Airbnb Deterministic Foundation"). No real sync orchestration calls this
/// yet (mandate §26: "NÃO implementar initial sync real ainda") — this
/// checkpoint's only caller is the deterministic import/duplicate/PII
/// acceptance tests (mandate §33-36), exercising the exact same real outbox/
/// Wolverine/RabbitMQ path a future real sync process will use.
/// </summary>
public interface IAirbnbReservationSyncPublisher
{
    /// <summary>
    /// <see cref="Result.Failure"/> with <see cref="AirbnbSyncErrorCodes.ListingMappingNotFound"/>
    /// when no mapping exists for <paramref name="externalListingId"/> for
    /// the current tenant — never publishes in that case.
    /// </summary>
    Task<Result> PublishReservationImportedAsync(
        string externalListingId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken);

    /// <inheritdoc cref="PublishReservationImportedAsync"/>
    Task<Result> PublishReservationUpdatedAsync(
        string externalListingId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken);

    /// <summary>No listing resolution needed — cancellation carries no PropertyId, so this never fails.</summary>
    Task PublishReservationCancelledAsync(
        string externalReservationId, DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken);
}
