using FluentAssertions;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 1 (Guest Operations Foundation) — proves
/// <see cref="RecordGuestCheckedOutCommandHandler"/> deterministically:
/// an Active operation transitions to CheckedOut and publishes
/// <see cref="GuestCheckedOut"/> exactly once; an already-CheckedOut
/// operation is a silent idempotent no-op; a missing operation (no
/// GuestStayOperation exists for the given ReservationId) throws a generic
/// anomaly exception and publishes nothing.
/// </summary>
public class RecordGuestCheckedOutCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateActiveOperation() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));

    private static RecordGuestCheckedOutCommand BuildCommand() => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
    };

    private static RecordGuestCheckedOutCommandHandler CreateHandler(
        FakeGuestStayOperationReader reader, RecordingGuestStayOperationRepository repository, FakeIntegrationEventCollector collector) =>
        new(reader, repository, collector, new PassThroughGuestOperationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<RecordGuestCheckedOutCommandHandler>.Instance);

    [Fact]
    public async Task HandleAsync_checks_out_an_Active_operation_and_publishes_GuestCheckedOut_once()
    {
        var operation = CreateActiveOperation();
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        operation.Status.Should().Be(GuestStayOperationStatus.CheckedOut);
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<GuestCheckedOut>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.ActorType.Should().Be("System");
    }

    [Fact]
    public async Task HandleAsync_when_already_CheckedOut_is_a_silent_no_op()
    {
        var operation = CreateActiveOperation();
        operation.CheckOut(Now.AddMinutes(1));
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id);
        var repository = RecordingGuestStayOperationRepository.WithOperation(operation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var act = async () => await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        repository.UpdateCallCount.Should().Be(0, "an already-CheckedOut operation must never be updated again");
        collector.EnqueuedEvents.Should().BeEmpty("an already-CheckedOut operation must never publish a duplicate GuestCheckedOut");
    }

    [Fact]
    public async Task HandleAsync_when_no_operation_exists_for_the_reservation_throws_and_publishes_nothing()
    {
        var reader = FakeGuestStayOperationReader.WithOperationIdResult(null);
        var repository = RecordingGuestStayOperationRepository.WithOperation(null);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(reader, repository, collector);

        var act = async () => await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
