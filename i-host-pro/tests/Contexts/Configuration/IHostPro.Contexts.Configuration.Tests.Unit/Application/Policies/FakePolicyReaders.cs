using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

internal sealed class FakeEarlyCheckInPolicyReader : IEarlyCheckInPolicyReader
{
    private readonly PolicyReadResult<EarlyCheckInPolicy> _result;

    private FakeEarlyCheckInPolicyReader(PolicyReadResult<EarlyCheckInPolicy> result) => _result = result;

    public static FakeEarlyCheckInPolicyReader Returning(PolicyReadResult<EarlyCheckInPolicy> result) => new(result);

    public Task<PolicyReadResult<EarlyCheckInPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) => Task.FromResult(_result);
}

internal sealed class FakeLateCheckoutPolicyReader : ILateCheckoutPolicyReader
{
    private readonly PolicyReadResult<LateCheckoutPolicy> _result;

    private FakeLateCheckoutPolicyReader(PolicyReadResult<LateCheckoutPolicy> result) => _result = result;

    public static FakeLateCheckoutPolicyReader Returning(PolicyReadResult<LateCheckoutPolicy> result) => new(result);

    public Task<PolicyReadResult<LateCheckoutPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) => Task.FromResult(_result);
}

internal sealed class FakeAiAgentBehaviorPolicyReader : IAiAgentBehaviorPolicyReader
{
    private readonly PolicyReadResult<AiAgentBehaviorPolicy> _result;

    private FakeAiAgentBehaviorPolicyReader(PolicyReadResult<AiAgentBehaviorPolicy> result) => _result = result;

    public static FakeAiAgentBehaviorPolicyReader Returning(PolicyReadResult<AiAgentBehaviorPolicy> result) => new(result);

    public Task<PolicyReadResult<AiAgentBehaviorPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) => Task.FromResult(_result);
}
