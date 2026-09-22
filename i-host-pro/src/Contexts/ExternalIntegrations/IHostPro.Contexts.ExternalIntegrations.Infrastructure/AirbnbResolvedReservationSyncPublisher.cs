using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// <inheritdoc cref="IAirbnbResolvedReservationSyncPublisher"/>
///
/// AIRBNB AUTO-PUBLISH REAL TRANSACTION REGRESSION PROOF (emergency gate):
/// deliberately does NOT open its own transaction. The only real caller
/// (<c>AirbnbEmailDeltaSyncRunner</c>) always invokes this from inside a
/// transaction it already owns via <c>IExternalIntegrationsTransactionExecutor</c>;
/// this method just enqueues the event into the already-ambient
/// <see cref="IIntegrationEventCollector"/>, which that SAME outer executor
/// drains and flushes atomically alongside the caller's own domain writes.
/// Opening a second transaction/executor call here previously nested a
/// second <c>TenantAwareTransactionScope.BeginAsync</c> on the same
/// <c>ExternalIntegrationsDbContext</c> instance, which always threw
/// <c>NestedUnitOfWorkException</c> — confirmed empirically by
/// <c>AirbnbAutoPublishNestedTransactionRegressionTests</c>.
/// </summary>
public sealed class AirbnbResolvedReservationSyncPublisher : IAirbnbResolvedReservationSyncPublisher
{
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ITenantContext _tenantContext;

    public AirbnbResolvedReservationSyncPublisher(
        IIntegrationEventCollector eventCollector,
        ITenantContext tenantContext)
    {
        _eventCollector = eventCollector;
        _tenantContext = tenantContext;
    }

    public Task PublishReservationImportedAsync(
        Guid propertyId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        DateTimeOffset occurredAtUtc, Guid correlationId, CancellationToken cancellationToken)
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

        return Task.CompletedTask;
    }

    private Guid CurrentTenantId() => _tenantContext.TenantId
        ?? throw new InvalidOperationException("No tenant resolved — the caller must set ITenantContext before publishing.");
}
