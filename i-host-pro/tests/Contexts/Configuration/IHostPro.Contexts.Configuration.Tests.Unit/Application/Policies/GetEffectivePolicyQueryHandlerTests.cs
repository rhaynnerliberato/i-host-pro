using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

public class GetEffectivePolicyQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Returns_policy_not_found_for_an_unknown_code()
    {
        var handler = new GetEffectivePolicyQueryHandler(
            FakeEarlyCheckInPolicyReader.Returning(PolicyReadResult<EarlyCheckInPolicy>.NotConfigured()),
            FakeLateCheckoutPolicyReader.Returning(PolicyReadResult<LateCheckoutPolicy>.NotConfigured()),
            FakeAiAgentBehaviorPolicyReader.Returning(PolicyReadResult<AiAgentBehaviorPolicy>.NotConfigured()));

        var result = await handler.Handle(new GetEffectivePolicyQuery(TenantId, "NOT_A_REAL_CODE", null), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.PolicyNotFound);
    }

    [Fact]
    public async Task Dispatches_EARLY_CHECKIN_to_the_early_check_in_reader()
    {
        var value = new EarlyCheckInPolicy(true, null, false, false, false);
        var handler = new GetEffectivePolicyQueryHandler(
            FakeEarlyCheckInPolicyReader.Returning(PolicyReadResult<EarlyCheckInPolicy>.Resolved(value, PolicyResolvedScope.Tenant, 1)),
            FakeLateCheckoutPolicyReader.Returning(PolicyReadResult<LateCheckoutPolicy>.NotConfigured()),
            FakeAiAgentBehaviorPolicyReader.Returning(PolicyReadResult<AiAgentBehaviorPolicy>.NotConfigured()));

        var result = await handler.Handle(new GetEffectivePolicyQuery(TenantId, "EARLY_CHECKIN", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PolicyReadStatus.Resolved);
        result.Value.Value.Should().Be(value);
        result.Value.ResolvedScope.Should().Be(PolicyResolvedScope.Tenant);
    }

    [Fact]
    public async Task Dispatches_LATE_CHECKOUT_to_the_late_checkout_reader()
    {
        var handler = new GetEffectivePolicyQueryHandler(
            FakeEarlyCheckInPolicyReader.Returning(PolicyReadResult<EarlyCheckInPolicy>.NotConfigured()),
            FakeLateCheckoutPolicyReader.Returning(PolicyReadResult<LateCheckoutPolicy>.NotConfigured()),
            FakeAiAgentBehaviorPolicyReader.Returning(PolicyReadResult<AiAgentBehaviorPolicy>.NotConfigured()));

        var result = await handler.Handle(new GetEffectivePolicyQuery(TenantId, "LATE_CHECKOUT", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(PolicyReadStatus.NotConfigured);
        result.Value.Value.Should().BeNull();
    }
}
