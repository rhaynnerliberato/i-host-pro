using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// <see cref="ISessionReader.ListActiveByUserIdAsync"/> already scopes to the
/// authenticated user (and, transitively, the current tenant — see its own
/// doc comment) and to active sessions only, so this handler does no
/// filtering of its own — only ordering (most recently active first, then by
/// id, for determinism — Incremento 3, Checkpoint 4, approved design) and the
/// <see cref="OwnSessionResult.IsCurrent"/> comparison against
/// <see cref="ListOwnSessionsQuery.CurrentSessionId"/>.
/// </summary>
public sealed class ListOwnSessionsQueryHandler : IQueryHandler<ListOwnSessionsQuery, IReadOnlyCollection<OwnSessionResult>>
{
    private readonly ISessionReader _sessionReader;

    public ListOwnSessionsQueryHandler(ISessionReader sessionReader) => _sessionReader = sessionReader;

    public async ValueTask<Result<IReadOnlyCollection<OwnSessionResult>>> Handle(
        ListOwnSessionsQuery query, CancellationToken cancellationToken)
    {
        var sessions = await _sessionReader.ListActiveByUserIdAsync(query.UserId, cancellationToken);

        var ordered = sessions
            .OrderByDescending(session => session.LastActivityAt)
            .ThenBy(session => session.Id)
            .Select(session => new OwnSessionResult(
                session.Id,
                session.CreatedAt,
                session.LastActivityAt,
                IsCurrent: session.Id == query.CurrentSessionId,
                session.Browser))
            .ToArray();

        return Result.Success<IReadOnlyCollection<OwnSessionResult>>(ordered);
    }
}
