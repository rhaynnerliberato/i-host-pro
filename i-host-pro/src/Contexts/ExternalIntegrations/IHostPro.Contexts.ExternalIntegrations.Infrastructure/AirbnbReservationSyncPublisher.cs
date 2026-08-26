using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingMappings;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <inheritdoc cref="IAirbnbReservationSyncPublisher"/>
/// <remarks>
/// Unlike <see cref="WhatsAppWebhookStatusEventPublisher"/>, this never opens
/// its own child DI scope — there is no multi-tenant-batched-payload problem
/// to solve here (a single import/update/cancel call is always for exactly
/// one, already-ambient tenant, resolved the ordinary way by whatever caller
/// set it up — ADR-016's own default, unlike the Meta webhook's one-delivery-
/// many-tenants exception).
/// </remarks>
public sealed class AirbnbReservationSyncPublisher : IAirbnbReservationSyncPublisher
{
    private readonly IAirbnbListingMappingRepository _listingMappingRepository;
    private readonly IExternalIntegrationsTransactionExecutor _transactionExecutor;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ITenantContext _tenantContext;

    public AirbnbReservationSyncPublisher(
        IAirbnbListingMappingRepository listingMappingRepository,
        IExternalIntegrationsTransactionExecutor transactionExecutor,
        IIntegrationEventCollector eventCollector,
        ITenantContext tenantContext)
    {
        _listingMappingRepository = listingMappingRepository;
        _transactionExecutor = transactionExecutor;
        _eventCollector = eventCollector;
        _tenantContext = tenantContext;
    }

    public Task<Result> PublishReservationImportedAsync(
        string externalListingId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var mapping = await _listingMappingRepository.GetByExternalListingIdAsync(externalListingId, cancellationToken);
            if (mapping is null)
                return ListingMappingNotFoundResult;

            _eventCollector.Enqueue(new AirbnbReservationImported
            {
                TenantId = CurrentTenantId(),
                AggregateId = Guid.NewGuid(),
                AggregateType = "AirbnbReservation",
                CorrelationId = correlationId,
                ActorType = "Integration",
                ExternalReservationId = externalReservationId,
                PropertyId = mapping.PropertyId,
                GuestName = guestName,
                CheckInAt = checkInAt,
                CheckOutAt = checkOutAt,
                GuestCount = guestCount,
                OccurredAtUtc = occurredAtUtc,
            });

            return Result.Success();
        }, cancellationToken);

    public Task<Result> PublishReservationUpdatedAsync(
        string externalListingId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var mapping = await _listingMappingRepository.GetByExternalListingIdAsync(externalListingId, cancellationToken);
            if (mapping is null)
                return ListingMappingNotFoundResult;

            _eventCollector.Enqueue(new AirbnbReservationUpdated
            {
                TenantId = CurrentTenantId(),
                AggregateId = Guid.NewGuid(),
                AggregateType = "AirbnbReservation",
                CorrelationId = correlationId,
                ActorType = "Integration",
                ExternalReservationId = externalReservationId,
                PropertyId = mapping.PropertyId,
                GuestName = guestName,
                CheckInAt = checkInAt,
                CheckOutAt = checkOutAt,
                GuestCount = guestCount,
                OccurredAtUtc = occurredAtUtc,
            });

            return Result.Success();
        }, cancellationToken);

    public Task PublishReservationCancelledAsync(
        string externalReservationId, DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _eventCollector.Enqueue(new AirbnbReservationCancelled
            {
                TenantId = CurrentTenantId(),
                AggregateId = Guid.NewGuid(),
                AggregateType = "AirbnbReservation",
                CorrelationId = correlationId,
                ActorType = "Integration",
                ExternalReservationId = externalReservationId,
                OccurredAtUtc = occurredAtUtc,
            });

            return Task.FromResult(true);
        }, cancellationToken);

    private Guid CurrentTenantId() => _tenantContext.TenantId
        ?? throw new InvalidOperationException("No tenant resolved — the caller must set ITenantContext before publishing.");

    private static readonly Result ListingMappingNotFoundResult =
        Result.Failure(new Error(AirbnbSyncErrorCodes.ListingMappingNotFound, AirbnbSyncErrorCodes.ListingMappingNotFound));
}
