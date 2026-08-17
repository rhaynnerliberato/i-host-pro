using Mediator;

namespace IHostPro.Contexts.Dashboard.Application;

/// <inheritdoc cref="IDashboardRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Dashboard.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="IDashboardRequestDispatcher"/>'s own doc comment.
/// </remarks>
internal sealed class DashboardRequestDispatcher : IDashboardRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public DashboardRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
