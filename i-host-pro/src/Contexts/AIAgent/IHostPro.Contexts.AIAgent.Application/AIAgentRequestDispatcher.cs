using Mediator;

namespace IHostPro.Contexts.AIAgent.Application;

/// <inheritdoc cref="IAIAgentRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project (<c>AIAgent.Application</c>)
/// as the <c>Mediator.Mediator</c> type it wraps — see
/// <see cref="IAIAgentRequestDispatcher"/>'s own doc comment.
/// </remarks>
internal sealed class AIAgentRequestDispatcher : IAIAgentRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public AIAgentRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
