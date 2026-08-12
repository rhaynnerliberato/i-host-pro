using Mediator;

namespace IHostPro.Contexts.Housekeeping.Application;

/// <inheritdoc cref="IHousekeepingRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Housekeeping.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="IHousekeepingRequestDispatcher"/>'s own doc
/// comment.
/// </remarks>
internal sealed class HousekeepingRequestDispatcher : IHousekeepingRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public HousekeepingRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
