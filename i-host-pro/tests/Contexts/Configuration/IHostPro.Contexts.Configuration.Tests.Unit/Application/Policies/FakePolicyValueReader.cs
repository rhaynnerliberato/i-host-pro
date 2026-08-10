using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

internal sealed class FakePolicyValueReader : IPolicyValueReader
{
    private readonly PolicyValueDetailResult? _current;
    private readonly IReadOnlyList<PolicyValueDetailResult> _history;

    private FakePolicyValueReader(PolicyValueDetailResult? current, IReadOnlyList<PolicyValueDetailResult> history)
    {
        _current = current;
        _history = history;
    }

    public static FakePolicyValueReader WithCurrent(PolicyValueDetailResult? current) => new(current, current is null ? [] : [current]);

    public static FakePolicyValueReader WithHistory(params PolicyValueDetailResult[] history) =>
        new(history.FirstOrDefault(v => v.IsCurrent), history);

    public Task<PolicyValueDetailResult?> GetCurrentAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_current);

    public Task<IReadOnlyList<PolicyValueDetailResult>> GetHistoryAsync(
        Guid tenantId, string policyCode, PolicyScopeType scopeType, Guid? scopeReferenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_history);
}
