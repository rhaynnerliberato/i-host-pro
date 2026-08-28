using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Payments.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ConfigurationChargeType = IHostPro.Contexts.Configuration.Contracts.LateCheckoutChargeType;
using DomainChargeType = IHostPro.Contexts.GuestOperations.Domain.Enums.LateCheckoutChargeType;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation) — proves
/// <see cref="PixChargeConfirmedLateCheckoutApprover"/> reuses the existing
/// CP3 approval path exactly, never duplicating logic, and is idempotent
/// against redelivery.
/// </summary>
public class PixChargeConfirmedLateCheckoutApproverTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RequestedCheckOutAt = Now.AddDays(1);

    private static LateCheckoutRequest CreatePendingPaymentRequest()
    {
        var request = LateCheckoutRequest.Create(
            Guid.NewGuid(), TenantId, ReservationId, PropertyId, RequestedCheckOutAt,
            DomainChargeType.FixedAmount, 100m, requiresPix: true, Now);
        request.MarkPendingPayment(Now);
        return request;
    }

    private static PixChargeConfirmed BuildMessage(Guid lateCheckoutRequestId) => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "PixCharge",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        LateCheckoutRequestId = lateCheckoutRequestId,
        ReservationId = ReservationId,
        ConfirmedAtUtc = Now,
    };

    private sealed class Context
    {
        public RecordingLateCheckoutRequestRepository Repository { get; } = new();
        public FakeLateCheckoutPolicyReader PolicyReader { get; init; } =
            FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.Resolved(
                new LateCheckoutPolicy(true, null, ConfigurationChargeType.FixedAmount, 100m, true, false, true), PolicyResolvedScope.Tenant, 1));
        public FakeIntegrationEventCollector EventCollector { get; } = new();

        public PixChargeConfirmedLateCheckoutApprover CreateApprover() => new(
            Repository, PolicyReader, EventCollector, new PassThroughGuestOperationsTransactionExecutor(),
            new FixedTimeProvider(Now), NullLogger<PixChargeConfirmedLateCheckoutApprover>.Instance);
    }

    [Fact]
    public async Task Approves_a_PendingPayment_request_and_publishes_LateCheckoutApproved()
    {
        var request = CreatePendingPaymentRequest();
        var ctx = new Context();
        ctx.Repository.AddedRequests.Add(request);

        await ctx.CreateApprover().HandleAsync(BuildMessage(request.Id), CancellationToken.None);

        request.Status.Should().Be(LateCheckoutRequestStatus.Approved);
        ctx.Repository.UpdateCallCount.Should().Be(1);
        ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<LateCheckoutApproved>();
        var published = (LateCheckoutApproved)ctx.EventCollector.EnqueuedEvents[0];
        published.ReservationId.Should().Be(ReservationId);
        published.UpdatesCleaning.Should().BeTrue("the policy's own UpdatesCleaning is re-read at confirmation time");
    }

    [Fact]
    public async Task Redelivered_confirmation_for_an_already_Approved_request_is_a_no_op()
    {
        var request = CreatePendingPaymentRequest();
        request.Approve(Now);
        var ctx = new Context();
        ctx.Repository.AddedRequests.Add(request);

        await ctx.CreateApprover().HandleAsync(BuildMessage(request.Id), CancellationToken.None);

        ctx.Repository.UpdateCallCount.Should().Be(0);
        ctx.EventCollector.EnqueuedEvents.Should().BeEmpty("a duplicate confirmation must never publish a second LateCheckoutApproved");
    }

    [Fact]
    public async Task Unknown_LateCheckoutRequestId_is_dropped_without_throwing()
    {
        var ctx = new Context();

        var act = async () => await ctx.CreateApprover().HandleAsync(BuildMessage(Guid.NewGuid()), CancellationToken.None);

        await act.Should().NotThrowAsync();
        ctx.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Unresolvable_policy_at_confirmation_time_defaults_UpdatesCleaning_to_false()
    {
        var request = CreatePendingPaymentRequest();
        var ctx = new Context
        {
            PolicyReader = FakeLateCheckoutPolicyReader.WithResult(PolicyReadResult<LateCheckoutPolicy>.NotConfigured()),
        };
        ctx.Repository.AddedRequests.Add(request);

        await ctx.CreateApprover().HandleAsync(BuildMessage(request.Id), CancellationToken.None);

        var published = (LateCheckoutApproved)ctx.EventCollector.EnqueuedEvents.Should().ContainSingle().Which;
        published.UpdatesCleaning.Should().BeFalse();
    }
}
