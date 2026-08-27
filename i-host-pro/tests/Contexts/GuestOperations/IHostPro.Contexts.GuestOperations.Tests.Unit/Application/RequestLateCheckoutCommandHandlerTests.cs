using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ConfigurationChargeType = IHostPro.Contexts.Configuration.Contracts.LateCheckoutChargeType;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 3 (Early Check-in / Late Checkout) — proves
/// <see cref="RequestLateCheckoutCommandHandler"/>'s full evaluation order
/// deterministically, mirroring <see cref="RequestEarlyCheckInCommandHandlerTests"/>'s
/// own structure, plus the two deliberate differences this handler adds: the
/// pre-row-creation Percentage-charge-type rejection, and the
/// RequiresPix-gated PendingPayment outcome (never a final Approved, never
/// publishes anything).
/// </summary>
public class RequestLateCheckoutCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentCheckInAt = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CurrentCheckOutAt = new(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestedCheckOutAt = new(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);

    private static GuestStayOperation CreateCheckedInOperation()
    {
        var operation = GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-2));
        operation.CheckIn(Now.AddDays(-1));
        return operation;
    }

    private static RequestLateCheckoutCommand BuildCommand(DateTimeOffset? requestedCheckOutAt = null) => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
        RequestedCheckOutAt = requestedCheckOutAt ?? RequestedCheckOutAt,
    };

    private static ReservationScheduleSnapshot ConfirmedSchedule() =>
        new("confirmed", CurrentCheckInAt, CurrentCheckOutAt);

    private static LateCheckoutPolicy AllowedPolicy(
        TimeOnly? latestTime = null, ConfigurationChargeType chargeType = ConfigurationChargeType.None,
        decimal? chargeValue = null, bool requiresPix = false, bool updatesCleaning = false) =>
        new(Allowed: true, LatestTime: latestTime, ChargeType: chargeType, ChargeValue: chargeValue,
            RequiresPix: requiresPix, BlocksCalendar: false, UpdatesCleaning: updatesCleaning);

    private sealed class Context
    {
        public FakeGuestStayOperationReader StayReader { get; init; } = FakeGuestStayOperationReader.WithOperationIdResult(null);
        public RecordingGuestStayOperationRepository StayRepository { get; init; } = RecordingGuestStayOperationRepository.WithOperation(null);
        public FakeLateCheckoutRequestReader RequestReader { get; init; } = FakeLateCheckoutRequestReader.WithActiveRequest(false);
        public RecordingLateCheckoutRequestRepository RequestRepository { get; } = new();
        public FakeReservationScheduleReader ScheduleReader { get; init; } = FakeReservationScheduleReader.WithSchedule(null);
        public FakeLateCheckoutPolicyReader PolicyReader { get; init; } =
            FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                new LateCheckoutPolicy(true, null, ConfigurationChargeType.None, null, false, false, false), PolicyResolvedScope.Tenant, 1));
        public FakeIntegrationEventCollector EventCollector { get; } = new();

        public RequestLateCheckoutCommandHandler CreateHandler() => new(
            StayReader, StayRepository, RequestReader, RequestRepository, ScheduleReader, PolicyReader,
            EventCollector, new PassThroughGuestOperationsTransactionExecutor(), new FixedTimeProvider(Now),
            NullLogger<RequestLateCheckoutCommandHandler>.Instance);
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
    public async Task Handle_fails_with_NotEligible_when_the_operation_is_still_Active()
    {
        var operation = GuestStayOperation.Create(Guid.NewGuid(), TenantId, ReservationId, PropertyId, Now.AddDays(-1));
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.GuestStayOperationNotEligibleForLateCheckout);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_ReservationNotFound_when_the_schedule_reader_returns_null()
    {
        var operation = CreateCheckedInOperation();
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
    public async Task Handle_fails_with_ReservationNotConfirmed_when_the_reservation_is_closed()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(new ReservationScheduleSnapshot("closed", CurrentCheckInAt, CurrentCheckOutAt)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.ReservationNotConfirmed);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_InvalidTime_when_the_requested_time_is_not_later_than_the_current_check_out()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(CurrentCheckOutAt), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.LateCheckoutRequestInvalidTime);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_AlreadyActive_when_a_PendingPayment_request_already_exists()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            RequestReader = FakeLateCheckoutRequestReader.WithActiveRequest(true),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.LateCheckoutRequestAlreadyActive);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_fails_with_PercentageUnsupported_and_creates_no_row_when_the_resolved_charge_type_is_Percentage()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                AllowedPolicy(chargeType: ConfigurationChargeType.Percentage, chargeValue: 10m), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(GuestOperationsErrorCodes.LateCheckoutChargeTypePercentageUnsupported);
        ctx.RequestRepository.AddedRequests.Should().BeEmpty("no row can snapshot a charge type this aggregate refuses to hold");
        ctx.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_denies_with_PolicyNotConfigured_creates_one_row_with_neutral_snapshot_and_publishes_one_event()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.NotConfigured()),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("denied");
        result.Value.DenialReasonCode.Should().Be(LateCheckoutDeniedReasonCodes.PolicyNotConfigured);
        result.Value.ChargeType.Should().Be("none");
        result.Value.ChargeValue.Should().BeNull();
        result.Value.RequiresPix.Should().BeFalse();
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        var published = ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutDenied>().Which;
        published.ReasonCode.Should().Be(LateCheckoutDeniedReasonCodes.PolicyNotConfigured);
    }

    [Fact]
    public async Task Handle_denies_with_PolicyNotAllowed_when_the_policy_disallows_late_checkout()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                new LateCheckoutPolicy(false, null, ConfigurationChargeType.None, null, false, false, false), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(LateCheckoutDeniedReasonCodes.PolicyNotAllowed);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutDenied>();
    }

    [Fact]
    public async Task Handle_denies_with_AfterLatestTime_when_the_requested_time_of_day_is_later_than_the_policy_allows()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                AllowedPolicy(latestTime: new TimeOnly(13, 0)), PolicyResolvedScope.Tenant, 1)),
        };

        // RequestedCheckOutAt is 14:00 — later than the policy's 13:00 ceiling.
        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(LateCheckoutDeniedReasonCodes.AfterLatestTime);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutDenied>();
    }

    [Fact]
    public async Task Handle_denies_with_ScheduleConflict_when_the_schedule_reader_reports_a_conflict()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule(), hasConflict: true),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                AllowedPolicy(), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DenialReasonCode.Should().Be(LateCheckoutDeniedReasonCodes.ScheduleConflict);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutDenied>();
    }

    [Fact]
    public async Task Handle_settles_at_PendingPayment_and_publishes_nothing_when_the_policy_requires_pix()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                AllowedPolicy(chargeType: ConfigurationChargeType.FixedAmount, chargeValue: 50m, requiresPix: true), PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("pending_payment");
        result.Value.RequiresPix.Should().BeTrue();
        result.Value.ChargeValue.Should().Be(50m);
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        ctx.RequestRepository.AddedRequests[0].Status.Should().Be(LateCheckoutRequestStatus.PendingPayment);
        ctx.EventCollector.EnqueuedEvents.Should().BeEmpty(
            "PendingPayment is not a final decision — Reservation's schedule must never change for it, and no event fires until Fase 10, Checkpoint 5");
    }

    [Fact]
    public async Task Handle_approves_and_publishes_LateCheckoutApproved_exactly_once_when_pix_is_not_required()
    {
        var operation = CreateCheckedInOperation();
        var ctx = new Context
        {
            StayReader = FakeGuestStayOperationReader.WithOperationIdResult(operation.Id),
            StayRepository = RecordingGuestStayOperationRepository.WithOperation(operation),
            ScheduleReader = FakeReservationScheduleReader.WithSchedule(ConfirmedSchedule()),
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                AllowedPolicy(chargeType: ConfigurationChargeType.FixedAmount, chargeValue: 30m, requiresPix: false, updatesCleaning: true),
                PolicyResolvedScope.Tenant, 1)),
        };

        var result = await ctx.CreateHandler().Handle(BuildCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("approved");
        result.Value.RequiresPix.Should().BeFalse();
        ctx.RequestRepository.AddedRequests.Should().ContainSingle();
        ctx.RequestRepository.AddedRequests[0].Status.Should().Be(LateCheckoutRequestStatus.Approved);
        var published = ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutApproved>().Which;
        published.ReservationId.Should().Be(ReservationId);
        published.ApprovedCheckOutAt.Should().Be(RequestedCheckOutAt);
        published.UpdatesCleaning.Should().BeTrue("the event must carry the SAME UpdatesCleaning value the approval decision itself used");
        published.ActorType.Should().Be("System");
    }
}
