using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

internal sealed class FakePolicyValueRepository : IPolicyValueRepository
{
    private readonly PolicyValue? _current;

    private FakePolicyValueRepository(PolicyValue? current) => _current = current;

    public static FakePolicyValueRepository WithCurrent(PolicyValue? current) => new(current);

    public List<PolicyValue> AddedValues { get; } = [];

    public Task<PolicyValue?> GetCurrentTrackedAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_current);

    public void Add(PolicyValue policyValue) => AddedValues.Add(policyValue);
}
