using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;

/// <summary>
/// Resolved-Property Publication Bridge gate — covers the one publisher
/// callers with an already-resolved PropertyId (e.g. the Email Bridge's
/// AirbnbListingTitleMapping) use instead of IAirbnbReservationSyncPublisher's
/// ExternalListingId-based resolution.
/// </summary>
public class AirbnbResolvedReservationSyncPublisherTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset CheckIn = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckOut = new(2026, 11, 8, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static (AirbnbResolvedReservationSyncPublisher Publisher, RecordingIntegrationEventCollector Collector) CreatePublisher()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(TenantId);
        var collector = new RecordingIntegrationEventCollector();
        var publisher = new AirbnbResolvedReservationSyncPublisher(collector, tenantContext);

        return (publisher, collector);
    }

    [Fact]
    public async Task PublishReservationImportedAsync_enqueues_AirbnbReservationImported_with_the_PropertyId_given_directly()
    {
        var (publisher, collector) = CreatePublisher();
        var correlationId = Guid.NewGuid();

        await publisher.PublishReservationImportedAsync(
            PropertyId, "TESTCODE12", "Hospede Teste", CheckIn, CheckOut, 2, OccurredAt, correlationId, CancellationToken.None);

        var published = collector.Enqueued.Should().ContainSingle().Which.Should().BeOfType<AirbnbReservationImported>().Subject;
        published.TenantId.Should().Be(TenantId);
        published.PropertyId.Should().Be(PropertyId);
        published.ExternalReservationId.Should().Be("TESTCODE12");
        published.GuestName.Should().Be("Hospede Teste");
        published.CheckInAt.Should().Be(CheckIn);
        published.CheckOutAt.Should().Be(CheckOut);
        published.GuestCount.Should().Be(2);
        published.OccurredAtUtc.Should().Be(OccurredAt);
        published.CorrelationId.Should().Be(correlationId);
        published.ActorType.Should().Be("Integration");
    }

    [Fact]
    public async Task PublishReservationImportedAsync_never_looks_up_a_listing_mapping_and_never_fails()
    {
        // No IAirbnbListingMappingRepository is even constructed here - the
        // PropertyId is trusted as already resolved by the caller, unlike
        // IAirbnbReservationSyncPublisher's ExternalListingId path.
        var (publisher, _) = CreatePublisher();

        var act = () => publisher.PublishReservationImportedAsync(
            PropertyId, "TESTCODE12", "Hospede Teste", CheckIn, CheckOut, 2, OccurredAt, Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishReservationImportedAsync_throws_when_no_tenant_is_resolved()
    {
        var collector = new RecordingIntegrationEventCollector();
        var publisher = new AirbnbResolvedReservationSyncPublisher(collector, new TenantContext());

        var act = () => publisher.PublishReservationImportedAsync(
            PropertyId, "TESTCODE12", "Hospede Teste", CheckIn, CheckOut, 2, OccurredAt, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
