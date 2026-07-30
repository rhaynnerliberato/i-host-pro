using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeSessionReader : ISessionReader
{
    private readonly IReadOnlyCollection<Session> _sessions;

    private FakeSessionReader(IReadOnlyCollection<Session> sessions) => _sessions = sessions;

    public static FakeSessionReader WithSessions(params Session[] sessions) => new(sessions);

    public int CallCount { get; private set; }
    public Guid? LastUserId { get; private set; }
    public CancellationToken? LastCancellationToken { get; private set; }

    // Both interface methods record into the same tracking fields — no
    // existing test needs to distinguish which one was called, and each
    // production caller (ListOwnSessionsQueryHandler vs. UserSessionRevoker)
    // already only ever calls exactly one of the two.
    public Task<IReadOnlyCollection<Session>> ListActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Record(userId, cancellationToken);

    public Task<IReadOnlyCollection<Session>> ListActiveForUpdateByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Record(userId, cancellationToken);

    private Task<IReadOnlyCollection<Session>> Record(Guid userId, CancellationToken cancellationToken)
    {
        CallCount++;
        LastUserId = userId;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(_sessions);
    }
}
