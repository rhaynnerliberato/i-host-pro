using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

/// <summary>
/// Covers every self-service "own cleaning" lifecycle command handler (Fase
/// 6, Incremento 2A) — MarkInTransit/Start/StartInspection/Complete/
/// WaitingMaterials/WaitingHelp/Delay. Each is structurally identical to its
/// administrative counterpart (already covered by
/// <see cref="CleaningLifecycleCommandHandlerTests"/>) plus one additional
/// axis: the ABAC ownership check (<c>OwnCleaningLoader</c>) — a caller
/// whose id does not match <c>AssignedHousekeeperUserId</c> must get the
/// exact same <c>cleaning_not_found</c> error as a nonexistent cleaning,
/// never a distinct "forbidden" signal.
/// </summary>
public class OwnCleaningLifecycleCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid HousekeeperUserId = Guid.NewGuid();
    private static readonly Guid OtherHousekeeperUserId = Guid.NewGuid();

    private static Cleaning AssignedCleaning(Guid? housekeeperUserId = null)
    {
        var cleaning = Cleaning.Create(Guid.NewGuid(), TenantId, Guid.NewGuid(), null, Guid.NewGuid(), Now.AddMinutes(-10));
        cleaning.Assign(housekeeperUserId ?? HousekeeperUserId, Now.AddMinutes(-9));
        return cleaning;
    }

    private static Cleaning InTransitCleaning()
    {
        var cleaning = AssignedCleaning();
        cleaning.MarkInTransit(Now.AddMinutes(-8));
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

    private static Cleaning CompletedCleaning()
    {
        var cleaning = InInspectionCleaning();
        cleaning.Complete(Now.AddMinutes(-6));
        return cleaning;
    }

    // --- MarkOwnCleaningInTransitCommandHandler: Assigned -> InTransit ---

    [Fact]
    public async Task MarkInTransit_from_Assigned_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningInTransit()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkOwnCleaningInTransitCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningInTransitCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("InTransit");

        var events = eventCollector.EnqueuedEvents.OfType<CleaningInTransit>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].AggregateId.Should().Be(cleaning.Id);
        events[0].AggregateType.Should().Be("Cleaning");
        events[0].CleaningId.Should().Be(cleaning.Id);
        events[0].ActorId.Should().Be(HousekeeperUserId.ToString());
    }

    [Fact]
    public async Task MarkInTransit_by_a_different_housekeeper_fails_with_CleaningNotFound_never_Forbidden_and_enqueues_no_event()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkOwnCleaningInTransitCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningInTransitCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
        eventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkInTransit_from_Started_fails_with_InvalidCleaningTransition_and_enqueues_no_event()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkOwnCleaningInTransitCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningInTransitCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
        eventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    // --- StartOwnCleaningCommandHandler: Assigned OR InTransit -> Started ---

    [Fact]
    public async Task Start_from_Assigned_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningStarted()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new StartOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartOwnCleaningCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Started");
        eventCollector.EnqueuedEvents.OfType<CleaningStarted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Start_from_InTransit_by_the_owning_housekeeper_succeeds()
    {
        var cleaning = InTransitCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new StartOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartOwnCleaningCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Started");
    }

    [Fact]
    public async Task Start_by_a_different_housekeeper_fails_with_CleaningNotFound_never_Forbidden()
    {
        var cleaning = AssignedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new StartOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartOwnCleaningCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    [Fact]
    public async Task Start_for_a_nonexistent_cleaning_fails_with_CleaningNotFound()
    {
        var repository = FakeCleaningRepository.WithCleaning(null);
        var handler = new StartOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new StartOwnCleaningCommand(TenantId, HousekeeperUserId, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- StartOwnCleaningInspectionCommandHandler: Started -> InInspection ---

    [Fact]
    public async Task StartInspection_from_Started_by_the_owning_housekeeper_succeeds()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new StartOwnCleaningInspectionCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new StartOwnCleaningInspectionCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("InInspection");
    }

    [Fact]
    public async Task StartInspection_by_a_different_housekeeper_fails_with_CleaningNotFound()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new StartOwnCleaningInspectionCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new StartOwnCleaningInspectionCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- CompleteOwnCleaningCommandHandler: InInspection -> Completed ---

    [Fact]
    public async Task Complete_from_InInspection_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningCompleted()
    {
        var cleaning = InInspectionCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CompleteOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new CompleteOwnCleaningCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Completed");
        eventCollector.EnqueuedEvents.OfType<CleaningCompleted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Complete_by_a_different_housekeeper_fails_with_CleaningNotFound()
    {
        var cleaning = InInspectionCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new CompleteOwnCleaningCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new CompleteOwnCleaningCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- MarkOwnCleaningWaitingMaterialsCommandHandler: Started -> WaitingMaterials ---

    [Fact]
    public async Task MarkWaitingMaterials_from_Started_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningNeedsMaterial()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkOwnCleaningWaitingMaterialsCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningWaitingMaterialsCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("WaitingMaterials");
        eventCollector.EnqueuedEvents.OfType<CleaningNeedsMaterial>().Should().ContainSingle();
    }

    [Fact]
    public async Task MarkWaitingMaterials_by_a_different_housekeeper_fails_with_CleaningNotFound()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new MarkOwnCleaningWaitingMaterialsCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningWaitingMaterialsCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- MarkOwnCleaningWaitingHelpCommandHandler: Started -> WaitingHelp ---

    [Fact]
    public async Task MarkWaitingHelp_from_Started_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningNeedsHelp()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new MarkOwnCleaningWaitingHelpCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(), eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningWaitingHelpCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("WaitingHelp");
        eventCollector.EnqueuedEvents.OfType<CleaningNeedsHelp>().Should().ContainSingle();
    }

    [Fact]
    public async Task MarkWaitingHelp_by_a_different_housekeeper_fails_with_CleaningNotFound()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new MarkOwnCleaningWaitingHelpCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new MarkOwnCleaningWaitingHelpCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    // --- ReportOwnCleaningDelayCommandHandler: no status change, publishes CleaningDelayed ---

    [Fact]
    public async Task ReportDelay_on_a_non_terminal_cleaning_by_the_owning_housekeeper_succeeds_and_enqueues_CleaningDelayed()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new ReportOwnCleaningDelayCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        var result = await handler.Handle(new ReportOwnCleaningDelayCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Started", "reporting a delay never changes the Cleaning's status");
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "cleaning_delayed");
        eventCollector.EnqueuedEvents.OfType<CleaningDelayed>().Should().ContainSingle();
    }

    [Fact]
    public async Task ReportDelay_on_a_Completed_cleaning_fails_with_InvalidCleaningTransition()
    {
        var cleaning = CompletedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new ReportOwnCleaningDelayCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new ReportOwnCleaningDelayCommand(TenantId, HousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    [Fact]
    public async Task ReportDelay_by_a_different_housekeeper_fails_with_CleaningNotFound()
    {
        var cleaning = StartedCleaning();
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var handler = new ReportOwnCleaningDelayCommandHandler(
            new PassThroughCleaningTransitionExecutor(), repository, new FakeHousekeepingAuditWriter(),
            new FakeIntegrationEventCollector(), new FixedTimeProvider(Now));

        var result = await handler.Handle(new ReportOwnCleaningDelayCommand(TenantId, OtherHousekeeperUserId, cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }
}
