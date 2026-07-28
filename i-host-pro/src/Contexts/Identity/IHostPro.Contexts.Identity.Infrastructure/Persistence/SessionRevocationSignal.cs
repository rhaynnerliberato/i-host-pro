using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="ISessionRevocationSignal"/>
/// <remarks>
/// Plain in-memory list — registered Scoped, one instance per request, never
/// accessed concurrently within that scope (handlers/executors run
/// sequentially, not in parallel, within a single request).
/// </remarks>
public sealed class SessionRevocationSignal : ISessionRevocationSignal
{
    private readonly List<(Guid TenantId, Guid SessionId)> _revocations = [];

    public void MarkRevoked(Guid tenantId, Guid sessionId) => _revocations.Add((tenantId, sessionId));

    public IReadOnlyCollection<(Guid TenantId, Guid SessionId)> Drain()
    {
        var drained = _revocations.ToArray();
        _revocations.Clear();
        return drained;
    }
}
