using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective
/// Implementation) — proves <see cref="RequestGuestAccessDeliveryCommandHandler"/>
/// deterministically: mutates NO domain state, publishes
/// <see cref="GuestAccessDeliveryRequested"/> only when both preconditions
/// hold (Reservation Confirmed, GuestStayOperation not CheckedOut).
/// </summary>
public class RequestGuestAccessDeliveryCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateActiveOperation() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));

    private static ReservationScheduleSnapshot ConfirmedSchedule() =>
        new("confirmed", Now.AddDays(1), Now.AddDays(4));

    private static RequestGuestAccessDeliveryCommand BuildCommand() => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
        ActorType = "User",
        ActorId = ActorId.ToString(),
    };

    private static RequestGuestAccessDeliveryCommandHandler CreateHandler(
        FakeGuestStayOperationReader reader, RecordingGuestStayOperationRepository repository,
        FakeReservationScheduleReader scheduleReader, FakeIntegrationEventCollector collector) =>
        new(reader, repository, scheduleReader, collector, new PassThroughGuestOperationsTransactionExecutor(),
            NullLogger<RequestGuestAccessDeliveryCommandHandler>.Instance);

    [Fact]
    public async Task Handle_publishes_GuestAccessDeliveryRequested_for_an_Active_confirmed_reservation()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule());
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repository.UpdateCallCount.Should().Be(0, "this command mutates no domain state of its own");
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<GuestAccessDeliveryRequested>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.PropertyId.Should().Be(PropertyId);
        published.AggregateId.Should().Be(operation.Id);
        published.AggregateType.Should().Be("GuestStayOperation");
        published.ActorType.Should().Be("User");
        published.ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task Handle_allows_a_CheckedIn_operation_explicitly()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddMinutes(1));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule());
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("resending access after check-in is a legitimate operational need");
        collector.EnqueuedEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_rejects_a_CheckedOut_operation_and_publishes_nothing()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddMinutes(1));
        operation.CheckOut(Now.AddMinutes(2));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule());
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_when_no_operation_exists_fails_with_NotFound_and_publishes_nothing()
    {
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(null);
        var repository = RecordingGuestStayOperationRepository.WithOperation(null);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule());
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotFound);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_when_reservation_does_not_exist_fails_with_ReservationNotFound_and_publishes_nothing()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(null);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.ReservationNotFound);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_when_reservation_is_not_confirmed_fails_with_ReservationNotConfirmed_and_publishes_nothing()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(new ReservationScheduleSnapshot("cancelled", Now.AddDays(1), Now.AddDays(4)));
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.ReservationNotConfirmed);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_republishes_on_every_call_idempotency_is_Communications_own_responsibility()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var scheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule());
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, scheduleReader, collector);

        await handler.Handle(BuildCommand(), CancellationToken.None);
        await handler.Handle(BuildCommand(), CancellationToken.None);

        collector.EnqueuedEvents.Should().HaveCount(2,
            "Guest Operations never tracks its own idempotency for this command — a repeated request republishes " +
            "every time; Communication's own per-intent idempotency key is what prevents a duplicate delivery");
    }
}
