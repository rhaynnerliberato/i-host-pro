using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeRefreshTokenReader : IRefreshTokenReader
{
    private readonly Dictionary<Guid, IReadOnlyCollection<RefreshToken>> _activeTokensBySessionId;

    private FakeRefreshTokenReader(Dictionary<Guid, IReadOnlyCollection<RefreshToken>> activeTokensBySessionId) =>
        _activeTokensBySessionId = activeTokensBySessionId;

    public static FakeRefreshTokenReader Empty() => new([]);

    public static FakeRefreshTokenReader WithActiveTokens(Guid sessionId, params RefreshToken[] tokens) =>
        new(new Dictionary<Guid, IReadOnlyCollection<RefreshToken>> { [sessionId] = tokens });

    public List<Guid> RequestedSessionIds { get; } = [];

    public Task<RefreshToken?> FindByTokenIdAsync(Guid tokenId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not exercised by UserSessionRevokerTests.");

    public Task<IReadOnlyCollection<RefreshToken>> FindActiveBySessionIdAsync(
        Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RequestedSessionIds.Add(sessionId);
        return Task.FromResult(_activeTokensBySessionId.TryGetValue(sessionId, out var tokens) ? tokens : []);
    }
}
