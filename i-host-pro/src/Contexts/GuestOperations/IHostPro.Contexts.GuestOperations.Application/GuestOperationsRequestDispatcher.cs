using Mediator;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <inheritdoc cref="IGuestOperationsRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>GuestOperations.Application</c>) as the <c>Mediator.Mediator</c> type
/// it wraps — see <see cref="IGuestOperationsRequestDispatcher"/>'s own doc
/// comment.
/// </remarks>
internal sealed class GuestOperationsRequestDispatcher : IGuestOperationsRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public GuestOperationsRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
