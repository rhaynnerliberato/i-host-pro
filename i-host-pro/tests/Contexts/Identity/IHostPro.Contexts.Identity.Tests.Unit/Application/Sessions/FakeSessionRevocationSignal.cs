using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeSessionRevocationSignal : ISessionRevocationSignal
{
    private readonly List<(Guid TenantId, Guid SessionId)> _marked = [];

    public IReadOnlyCollection<(Guid TenantId, Guid SessionId)> MarkedRevocations => _marked;

    public void MarkRevoked(Guid tenantId, Guid sessionId) => _marked.Add((tenantId, sessionId));

    public IReadOnlyCollection<(Guid TenantId, Guid SessionId)> Drain()
    {
        var drained = _marked.ToArray();
        _marked.Clear();
        return drained;
    }
}
