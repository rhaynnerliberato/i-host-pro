using Mediator;

namespace IHostPro.Contexts.Reservations.Application;

/// <inheritdoc cref="IReservationsRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Reservations.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="IReservationsRequestDispatcher"/>'s own doc
/// comment.
/// </remarks>
internal sealed class ReservationsRequestDispatcher : IReservationsRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public ReservationsRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
