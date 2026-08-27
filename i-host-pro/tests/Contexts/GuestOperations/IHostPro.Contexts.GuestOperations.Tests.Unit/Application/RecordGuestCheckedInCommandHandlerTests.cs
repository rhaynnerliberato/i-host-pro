using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 2 (Check-in/Checkout Core) — proves
/// <see cref="RecordGuestCheckedInCommandHandler"/> deterministically: an
/// Active operation transitions to CheckedIn and publishes
/// <see cref="GuestCheckedIn"/> exactly once; an already-CheckedIn operation
/// is a silent idempotent no-op; an already-CheckedOut operation is a
/// terminal-state <see cref="GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut"/>
/// failure, never restored; a missing operation is a
/// <see cref="GuestOperationsErrorCodes.GuestStayOperationNotFound"/> failure.
/// Neither failure publishes anything.
/// </summary>
public class RecordGuestCheckedInCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateActiveOperation() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));

    private static RecordGuestCheckedInCommand BuildCommand() => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
    };

    private static RecordGuestCheckedInCommandHandler CreateHandler(
        FakeGuestStayOperationReader reader, RecordingGuestStayOperationRepository repository, FakeIntegrationEventCollector collector) =>
        new(reader, repository, collector, new PassThroughGuestOperationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<RecordGuestCheckedInCommandHandler>.Instance);

    [Fact]
    public async Task Handle_checks_in_an_Active_operation_and_publishes_GuestCheckedIn_once()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        operation.Status.Should().Be(GuestStayOperationStatus.CheckedIn);
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<GuestCheckedIn>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.ActorType.Should().Be("System");
    }

    [Fact]
    public async Task Handle_when_already_CheckedIn_is_a_silent_no_op()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddMinutes(1));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repository.UpdateCallCount.Should().Be(0, "an already-CheckedIn operation must never be updated again");
        collector.EnqueuedEvents.Should().BeEmpty("an already-CheckedIn operation must never publish a duplicate GuestCheckedIn");
    }

    [Fact]
    public async Task Handle_when_already_CheckedOut_fails_with_AlreadyCheckedOut_and_publishes_nothing()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddMinutes(1));
        operation.CheckOut(Now.AddMinutes(2));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut);
        repository.UpdateCallCount.Should().Be(0);
        collector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_when_no_operation_exists_for_the_reservation_fails_with_NotFound_and_publishes_nothing()
    {
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(null);
        var repository = RecordingGuestStayOperationRepository.WithOperation(null);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotFound);
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
