using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 3 (Early Check-in / Late Checkout) — proves
/// <see cref="RequestEarlyCheckInCommandHandler"/>'s full evaluation order
/// deterministically: every precondition/validation failure returns a
/// <see cref="BuildingBlocks.Domain.Result{TValue}"/> failure BEFORE any
/// <see cref="EarlyCheckInRequest"/> row is created; every policy-driven
/// outcome (NotConfigured/NotAllowed/BeforeEarliestTime/ScheduleConflict/
/// CleaningNotReady/Approved) creates exactly one row and publishes exactly
/// one Integration Event.
/// </summary>
public class RequestEarlyCheckInCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentCheckInAt = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentCheckOutAt = new(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestedCheckInAt = new(2026, 9, 1, 13, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateActiveOperation() =>
        GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));

    private static RequestEarlyCheckInCommand BuildCommand(DateTimeOffset? requestedCheckInAt = null) => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
        RequestedCheckInAt = requestedCheckInAt ?? RequestedCheckInAt,
    };

    private static ReservationScheduleSnapshot ConfirmedSchedule() =>
        new("confirmed", CurrentCheckInAt, CurrentCheckOutAt);

    private static EarlyCheckInPolicy AllowedPolicy(
        TimeOnly? earliestTime = null, bool requiresCleaningCompleted = false) =>
        new(Allowed: true, EarliestTime: earliestTime, RequiresCleaningCompleted: requiresCleaningCompleted, RequiresForm: false, NotifyFrontDesk: false);

    private sealed class Context
    {
        public FakeGuestStayOperationReader StayReader { get; init; } = FakeGuestStayOperationReader.WithOperationIdResult(null);
        public RecordingGuestStayOperationRepository StayRepository { get; init; } = RecordingGuestStayOperationRepository.WithOperation(null);
        public FakeEarlyCheckInRequestReader RequestReader { get; init; } = FakeEarlyCheckInRequestReader.WithActiveRequest(false);
        public RecordingEarlyCheckInRequestRepository RequestRepository { get; } = new();
        public FakeReservationScheduleReader ScheduleReader { get; init; } = FakeReservationScheduleReader.WithSchedule(null);
        public FakeCleaningReadinessReader CleaningReader { get; init; } = FakeCleaningReadinessReader.WithCompleted(true);
        public FakeEarlyCheckInPolicyReader PolicyReader { get; init; } =
            FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                new EarlyCheckInPolicy(true, null, false, false, false), PolicyResolvedScope.Tenant, 1));
        public FakeIntegrationEventCollector EventCollector { get; } = new();

        public RequestEarlyCheckInCommandHandler CreateHandler() => new(
            StayReader, StayRepository, RequestReader, RequestRepository, ScheduleReader, CleaningReader, PolicyReader,
            EventCollector, new PassThroughGuestOperationsTransactionExecutor(), new FixedTimeProvider(Now),
            NullLogger<RequestEarlyCheckInCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_fails_with_NotFound_when_no_GuestStayOperation_exists_and_creates_no_row()
    {
        var ctx = new Context();

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotFound);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
        ctx.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_NotEligible_when_the_operation_is_already_CheckedIn()
    {
        var operation = CreateActiveOperation();
        operation.CheckIn(Now.AddMinutes(-30));
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotEligibleForEarlyCheckIn);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_ReservationNotFound_when_the_schedule_reader_returns_null()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(null),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.ReservationNotFound);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_ReservationNotConfirmed_when_the_reservation_is_cancelled()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(new ReservationScheduleSnapshot("cancelled", CurrentCheckInAt, CurrentCheckOutAt)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.ReservationNotConfirmed);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_InvalidTime_when_the_requested_time_is_not_earlier_than_the_current_check_in()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(CurrentCheckInAt), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.EarlyCheckInRequestInvalidTime);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_AlreadyActive_when_a_Pending_request_already_exists()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            RequestReader = FakeEarlyCheckInRequestReader.WithActiveRequest(true),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.EarlyCheckInRequestAlreadyActive);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_denies_with_PolicyNotConfigured_creates_one_row_and_publishes_one_event()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.NotConfigured()),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("denied");
        result.Value.DenialReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.PolicyNotConfigured);
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        var published = ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinDenied>().Which;
        published.ReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.PolicyNotConfigured);
    }

    [Fact]
    public async Task Handle_denies_with_PolicyNotAllowed_when_the_policy_disallows_early_check_in()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                new EarlyCheckInPolicy(false, null, false, false, false), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.PolicyNotAllowed);
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinDenied>();
    }

    [Fact]
    public async Task Handle_denies_with_BeforeEarliestTime_when_the_requested_time_of_day_is_earlier_than_the_policy_allows()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                AllowedPolicy(earliestTime: new TimeOnly(14, 0)), PolicyResolvedScope.Tenant, 1)),
        };

        // RequestedCheckInAt is 13:00 — earlier than the policy's 14:00 floor.
        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.BeforeEarliestTime);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinDenied>();
    }

    [Fact]
    public async Task Handle_denies_with_ScheduleConflict_when_the_schedule_reader_reports_a_conflict()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule(), hasConflict: true),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                AllowedPolicy(), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.ScheduleConflict);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinDenied>();
    }

    [Fact]
    public async Task Handle_denies_with_CleaningNotReady_when_the_policy_requires_it_and_cleaning_is_not_completed()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            CleaningReader = FakeCleaningReadinessReader.WithCompleted(false),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                AllowedPolicy(requiresCleaningCompleted: true), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(EarlyCheckinDeniedReasonCodes.CleaningNotReady);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinDenied>();
    }

    [Fact]
    public async Task Handle_approves_when_cleaning_is_not_required_by_policy_even_though_it_is_not_completed()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            CleaningReader = FakeCleaningReadinessReader.WithCompleted(false),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                AllowedPolicy(requiresCleaningCompleted: false), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("approved");
        result.Value.DenialReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Handle_approves_and_publishes_EarlyCheckinApproved_exactly_once_when_every_check_passes()
    {
        var operation = CreateActiveOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            CleaningReader = FakeCleaningReadinessReader.WithCompleted(true),
            PolicyReader = FakeEarlyCheckInPolicyReader.WithResult(PolicyReadResult<EarlyCheckInPolicy>.Resolved(
                AllowedPolicy(requiresCleaningCompleted: true), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("approved");
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        ctx.RequestRepository.AddedRequests[0].Status.Should().Be(EarlyCheckInRequestStatus.Approved);
        var published = ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<EarlyCheckinApproved>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.ApprovedCheckInAt.Should().Be(RequestedCheckInAt);
        published.ActorType.Should().Be("System");
    }
}
