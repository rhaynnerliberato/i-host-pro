using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetRelevantPoliciesToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ReservationResult BuildReservation() => new(
        Context.ReservationId, PropertyId, "Guest", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), 2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static FakeReservationsRequestDispatcher BuildReservationsDispatcher()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        return dispatcher;
    }

    [Fact]
    public async Task ExecuteAsync_with_an_explicit_code_returns_only_that_policy_typed_safely()
    {
        var reservationsDispatcher = BuildReservationsDispatcher();
        var configurationDispatcher = new FakeConfigurationRequestDispatcher();
        var earlyCheckIn = new EarlyCheckInPolicy(true, new TimeOnly(12, 0), true, false, true);
        var policyResult = new EffectivePolicyResult("EARLY_CHECKIN", PolicyReadStatus.Resolved, earlyCheckIn, PolicyResolvedScope.Property, 1);
        configurationDispatcher.Stub.SetResponse(
            new GetEffectivePolicyQuery(Context.TenantId, "EARLY_CHECKIN", PropertyId), Result.Success(policyResult));
        var tool = new GetRelevantPoliciesTool(reservationsDispatcher, configurationDispatcher);

        var result = await tool.ExecuteAsync(
            Context, new Dictionary<string, string> { ["policyCode"] = "EARLY_CHECKIN" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("EARLY_CHECKIN");
        result.Content.Should().Contain("12:00");
        result.Content.Should().NotContain("LATE_CHECKOUT");
    }

    [Fact]
    public async Task ExecuteAsync_with_no_argument_reads_both_policies()
    {
        var reservationsDispatcher = BuildReservationsDispatcher();
        var configurationDispatcher = new FakeConfigurationRequestDispatcher();
        var earlyCheckIn = new EarlyCheckInPolicy(false, null, false, false, false);
        var lateCheckout = new LateCheckoutPolicy(true, new TimeOnly(14, 0), LateCheckoutChargeType.FixedAmount, 50m, true, true, true);
        configurationDispatcher.Stub.SetResponse(
            new GetEffectivePolicyQuery(Context.TenantId, "EARLY_CHECKIN", PropertyId),
            Result.Success(new EffectivePolicyResult("EARLY_CHECKIN", PolicyReadStatus.Resolved, earlyCheckIn, PolicyResolvedScope.Global, null)));
        configurationDispatcher.Stub.SetResponse(
            new GetEffectivePolicyQuery(Context.TenantId, "LATE_CHECKOUT", PropertyId),
            Result.Success(new EffectivePolicyResult("LATE_CHECKOUT", PolicyReadStatus.Resolved, lateCheckout, PolicyResolvedScope.Property, 3)));
        var tool = new GetRelevantPoliciesTool(reservationsDispatcher, configurationDispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("EARLY_CHECKIN");
        result.Content.Should().Contain("LATE_CHECKOUT");
        result.Content.Should().Contain("14:00");
    }

    [Fact]
    public async Task ExecuteAsync_reports_not_configured_without_inventing_a_value()
    {
        var reservationsDispatcher = BuildReservationsDispatcher();
        var configurationDispatcher = new FakeConfigurationRequestDispatcher();
        configurationDispatcher.Stub.SetResponse(
            new GetEffectivePolicyQuery(Context.TenantId, "EARLY_CHECKIN", PropertyId),
            Result.Success(new EffectivePolicyResult("EARLY_CHECKIN", PolicyReadStatus.NotConfigured, null, null, null)));
        var tool = new GetRelevantPoliciesTool(reservationsDispatcher, configurationDispatcher);

        var result = await tool.ExecuteAsync(
            Context, new Dictionary<string, string> { ["policyCode"] = "EARLY_CHECKIN" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("não configurada");
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_policy_code_outside_the_allowlist()
    {
        var reservationsDispatcher = new FakeReservationsRequestDispatcher();
        var configurationDispatcher = new FakeConfigurationRequestDispatcher();
        var tool = new GetRelevantPoliciesTool(reservationsDispatcher, configurationDispatcher);

        var result = await tool.ExecuteAsync(
            Context, new Dictionary<string, string> { ["policyCode"] = "NOT_A_REAL_CODE" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("invalid_policy_code");
        reservationsDispatcher.Stub.ReceivedRequests.Should().BeEmpty("an invalid code fails before any dispatcher call");
    }
}
