using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 1 (Guest Operations Foundation) — Checkpoint 2
/// (Check-in/Checkout Core, checkout now requires a prior check-in and
/// returns <c>Result&lt;GuestStayOperationResult&gt;</c>, never a thrown
/// exception, since it is now HTTP-exposed) — proves
/// <see cref="RecordGuestCheckedOutCommandHandler"/> deterministically: a
/// CheckedIn operation transitions to CheckedOut and publishes
/// <see cref="GuestCheckedOut"/> exactly once; an already-CheckedOut
/// operation is a silent idempotent no-op; a checkout attempted while still
/// Active (never checked in) is a <see cref="GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn"/>
/// failure; a missing operation is a <see cref="GuestOperationsErrorCodes.GuestStayOperationNotFound"/>
/// failure. Neither failure publishes anything.
/// </summary>
public class RecordGuestCheckedOutCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateActiveOperation() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));

    private static GuestStayOperation CreateCheckedInOperation()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddHours(-1));
        return operation;
    }

    private static RecordGuestCheckedOutCommand BuildCommand() => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
        ActorId = ActorId,
    };

    private static RecordGuestCheckedOutCommandHandler CreateHandler(
        FakeGuestStayOperationReader reader, RecordingGuestStayOperationRepository repository, FakeIntegrationEventCollector collector) =>
        new(reader, repository, collector, new PassThroughGuestOperationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<RecordGuestCheckedOutCommandHandler>.Instance);

    [Fact]
    public async Task Handle_checks_out_a_CheckedIn_operation_and_publishes_GuestCheckedOut_once()
    {
        var operation = CreateCheckedInOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        operation.Status.Should().Be(GuestStayOperationStatus.CheckedOut);
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<GuestCheckedOut>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.ActorType.Should().Be("User");
        published.ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task Handle_when_already_CheckedOut_is_a_silent_no_op()
    {
        var operation = CreateCheckedInOperation();
        operation.CheckOut(Now.AddMinutes(1));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repository.UpdateCallCount.Should().Be(0, "an already-CheckedOut operation must never be updated again");
        collector.EnqueuedEvents.Should().BeEmpty("an already-CheckedOut operation must never publish a duplicate GuestCheckedOut");
    }

    [Fact]
    public async Task Handle_when_still_Active_fails_with_NotCheckedIn_and_publishes_nothing()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn);
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
