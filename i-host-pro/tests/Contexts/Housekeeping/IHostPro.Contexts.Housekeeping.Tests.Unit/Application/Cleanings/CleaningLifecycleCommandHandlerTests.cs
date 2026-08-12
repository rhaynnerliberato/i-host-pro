using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

/// <summary>
/// Covers every "existing Cleaning, single guarded transition" command
/// handler — Start/StartInspection/Complete/Cancel/MarkInterrupted/
/// MarkWaitingMaterials/MarkWaitingHelp — each structurally near-identical
/// (load by id, guard InvalidOperationException -> InvalidCleaningTransition,
/// call the domain method, audit, optionally enqueue an Integration Event).
/// </summary>
public class CleaningLifecycleCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static Cleaning PendingCleaning() =>
        Cleaning.Create(Guid.NewGuid(), TenantId, Guid.NewGuid(), null, Guid.NewGuid(), Now.AddMinutes(-10));

    private static Cleaning AssignedCleaning()
    {
        var cleaning = PendingCleaning();
        cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(-9));
        return cleaning;
    }

    private static Cleaning StartedCleaning()
    {
        var cleaning = AssignedCleaning();
        cleaning.Start(Now.AddMinutes(-8));
        return cleaning;
    }

    private static Cleaning InInspectionCleaning()
    {
        var cleaning = StartedCleaning();
        cleaning.StartInspection(Now.AddMinutes(-7));
        return cleaning;
    }

    // --- StartCleaningCommandHandler: Assigned -> Started ---

    [Fact]
    public async Task Start_from_Assigned_succeeds_audits_and_enqueues_CleaningStarted()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new StartCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Started");
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "cleaning_started");
        eventCollector.EnqueuedEvents.OfType<CleaningStarted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Start_from_Pending_fails_with_InvalidCleaningTransition_and_performs_no_side_effect()
    {
        var cleaning = PendingCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new StartCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
        auditWriter.RecordedEntries.Should().BeEmpty();
        eventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_for_a_nonexistent_cleaning_fails_with_CleaningNotFound()
    {
        var repository = FakeCleaningRepository.WithCleaning(null);
        var handler = new StartCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartCleaningCommand(TenantId, ActorId, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- StartCleaningInspectionCommandHandler: Started -> InInspection ---

    [Fact]
    public async Task StartInspection_from_Started_succeeds_audits_and_enqueues_CleaningInspectionStarted()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new StartCleaningInspectionCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartCleaningInspectionCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("InInspection");
        eventCollector.EnqueuedEvents.OfType<CleaningInspectionStarted>().Should().ContainSingle();
    }

    [Fact]
    public async Task StartInspection_from_Assigned_fails_with_InvalidCleaningTransition()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new StartCleaningInspectionCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartCleaningInspectionCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    // --- CompleteCleaningCommandHandler: InInspection -> Completed (terminal) ---

    [Fact]
    public async Task Complete_from_InInspection_succeeds_audits_and_enqueues_CleaningCompleted_with_PropertyId()
    {
        var cleaning = InInspectionCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CompleteCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new CompleteCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Completed");
        var events = eventCollector.EnqueuedEvents.OfType<CleaningCompleted>().ToArray();
        events.Should().ContainSingle();
        events[0].PropertyId.Should().Be(cleaning.PropertyId);
    }

    [Fact]
    public async Task Complete_from_Started_fails_with_InvalidCleaningTransition()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new CompleteCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new CompleteCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    [Fact]
    public async Task Completing_an_already_Completed_cleaning_fails_terminal_state_is_enforced()
    {
        var cleaning = InInspectionCleaning();
        cleaning.Complete(Now.AddMinutes(-6));
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new CompleteCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new CompleteCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    // --- CancelCleaningCommandHandler: Pending/Assigned -> Cancelled (terminal) ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_from_Pending_or_Assigned_succeeds_audits_and_enqueues_CleaningCancelled(bool assignFirst)
    {
        var cleaning = assignFirst ? AssignedCleaning() : PendingCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CancelCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new CancelCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Cancelled");
        eventCollector.EnqueuedEvents.OfType<CleaningCancelled>().Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_from_Started_fails_no_documented_direct_cancel_path()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new CancelCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new CancelCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    [Fact]
    public async Task Cancelling_an_already_Cancelled_cleaning_fails_terminal_state_is_enforced()
    {
        var cleaning = PendingCleaning();
        cleaning.Cancel(Now.AddMinutes(-9));
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new CancelCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new CancelCleaningCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    // --- MarkCleaningInterruptedCommandHandler: Started -> Interrupted (no event) ---

    [Fact]
    public async Task MarkInterrupted_from_Started_succeeds_and_audits_but_enqueues_no_event()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var handler = new MarkCleaningInterruptedCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningInterruptedCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Interrupted");
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "cleaning_interrupted");
    }

    [Fact]
    public async Task MarkInterrupted_from_Assigned_fails_with_InvalidCleaningTransition()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new MarkCleaningInterruptedCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningInterruptedCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    // --- MarkCleaningWaitingMaterialsCommandHandler: Started -> WaitingMaterials (publishes CleaningNeedsMaterial, Fase 6 Incremento 2A) ---

    [Fact]
    public async Task MarkWaitingMaterials_from_Started_succeeds_audits_and_enqueues_CleaningNeedsMaterial()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkCleaningWaitingMaterialsCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningWaitingMaterialsCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("WaitingMaterials");
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "cleaning_waiting_materials");
        eventCollector.EnqueuedEvents.Should().ContainSingle(e => e is CleaningNeedsMaterial);
    }

    [Fact]
    public async Task MarkWaitingMaterials_from_Pending_fails_with_InvalidCleaningTransition()
    {
        var cleaning = PendingCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new MarkCleaningWaitingMaterialsCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningWaitingMaterialsCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    // --- MarkCleaningWaitingHelpCommandHandler: Started -> WaitingHelp (publishes CleaningNeedsHelp, Fase 6 Incremento 2A) ---

    [Fact]
    public async Task MarkWaitingHelp_from_Started_succeeds_audits_and_enqueues_CleaningNeedsHelp()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkCleaningWaitingHelpCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningWaitingHelpCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("WaitingHelp");
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "cleaning_waiting_help");
        eventCollector.EnqueuedEvents.Should().ContainSingle(e => e is CleaningNeedsHelp);
    }

    [Fact]
    public async Task MarkWaitingHelp_from_InInspection_fails_with_InvalidCleaningTransition()
    {
        var cleaning = InInspectionCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new MarkCleaningWaitingHelpCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new MarkCleaningWaitingHelpCommand(TenantId, ActorId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }
}
