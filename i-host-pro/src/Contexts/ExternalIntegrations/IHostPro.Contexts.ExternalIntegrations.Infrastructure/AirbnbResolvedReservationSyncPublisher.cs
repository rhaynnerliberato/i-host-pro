using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <inheritdoc cref="IAirbnbResolvedReservationSyncPublisher"/>
public sealed class AirbnbResolvedReservationSyncPublisher : IAirbnbResolvedReservationSyncPublisher
{
    private readonly IExternalIntegrationsTransactionExecutor _transactionExecutor;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ITenantContext _tenantContext;

    public AirbnbResolvedReservationSyncPublisher(
        IExternalIntegrationsTransactionExecutor transactionExecutor,
        IIntegrationEventCollector eventCollector,
        ITenantContext tenantContext)
    {
        _transactionExecutor = transactionExecutor;
        _eventCollector = eventCollector;
        _tenantContext = tenantContext;
    }

    public Task PublishReservationImportedAsync(
        Guid propertyId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            _eventCollector.Enqueue(new AirbnbReservationImported
            {
                TenantId = CurrentTenantId(),
                AggregateId = Guid.NewGuid(),
                AggregateType = "AirbnbReservation",
                CorrelationId = correlationId,
                ActorType = "Integration",
                ExternalReservationId = externalReservationId,
                PropertyId = propertyId,
                GuestName = guestName,
                CheckInAt = checkInAt,
                CheckOutAt = checkOutAt,
                GuestCount = guestCount,
                OccurredAtUtc = occurredAtUtc,
            });

            return Task.FromResult(true);
        }, cancellationToken);

    private Guid CurrentTenantId() => _tenantContext.TenantId
        ?? throw new InvalidOperationException("No tenant resolved — the caller must set ITenantContext before publishing.");
}
