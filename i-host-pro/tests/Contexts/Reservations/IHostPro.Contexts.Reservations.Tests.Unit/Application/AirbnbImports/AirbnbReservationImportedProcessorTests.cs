using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application.AirbnbImports;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.AirbnbImports;

public class AirbnbReservationImportedProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static AirbnbReservationImported BuildEvent(string externalReservationId) => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "AirbnbReservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ExternalReservationId = externalReservationId,
        PropertyId = PropertyId,
        GuestName = "Guest",
        CheckInAt = Now.AddDays(1),
        CheckOutAt = Now.AddDays(3),
        GuestCount = 2,
        OccurredAtUtc = Now,
    };

    private static AirbnbReservationImportedProcessor CreateProcessor(
        FakeReservationReader reader, RecordingReservationRepository repository, FakeIntegrationEventCollector collector) =>
        new(reader, repository, collector, new PassThroughReservationsTransactionExecutor(), new FixedTimeProvider(Now),
            NullLogger<AirbnbReservationImportedProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_creates_a_real_Reservation_when_the_external_id_is_unknown()
    {
        var reader = FakeReservationReader.WithExternalIdentityResult(null);
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reader, repository, collector);

        await processor.HandleAsync(BuildEvent("AIRBNB-1"), CancellationToken.None);

        repository.AddCallCount.Should().Be(1);
        var reservation = repository.AddedReservations.Should().ContainSingle().Which;
        reservation.Source.Should().Be(ReservationSource.Airbnb);
        reservation.ExternalReservationId.Should().Be("AIRBNB-1");
        reservation.PropertyId.Should().Be(PropertyId);
        reservation.GuestPhone.Should().BeNull();

        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<ReservationCreated>().Which;
        published.Source.Should().Be("airbnb");
        published.ReservationId.Should().Be(reservation.Id);
    }

    [Fact]
    public async Task HandleAsync_is_a_no_op_when_the_external_id_is_already_imported()
    {
        var reader = FakeReservationReader.WithExternalIdentityResult(Guid.NewGuid());
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reader, repository, collector);

        await processor.HandleAsync(BuildEvent("AIRBNB-2"), CancellationToken.None);

        repository.AddCallCount.Should().Be(0, "a redelivered/duplicate import must never create a second Reservation");
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
