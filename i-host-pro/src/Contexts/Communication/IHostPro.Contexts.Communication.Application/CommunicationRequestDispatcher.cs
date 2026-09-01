using Mediator;

namespace IHostPro.Contexts.Communication.Application;

/// <inheritdoc cref="ICommunicationRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Communication.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="ICommunicationRequestDispatcher"/>'s own doc comment.
/// </remarks>
internal sealed class CommunicationRequestDispatcher : ICommunicationRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public CommunicationRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
