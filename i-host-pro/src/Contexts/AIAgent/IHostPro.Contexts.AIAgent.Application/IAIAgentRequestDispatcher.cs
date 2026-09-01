using Mediator;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// This Bounded Context's own request dispatcher — mirrors
/// <c>ICommunicationRequestDispatcher</c>/<c>IPaymentsRequestDispatcher</c>
/// exactly: <c>Mediator.SourceGenerator</c> generates a distinct,
/// assembly-scoped concrete <c>Mediator.Mediator</c> type per project, and
/// the shared <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c>
/// interfaces become ambiguous once every Bounded Context registers itself
/// in the same container (<c>IHostPro.Api</c> composes all of them). This
/// interface exposes only the single overload the one real consumer
/// (<c>IHostPro.Api</c>'s Resume-session endpoint) actually calls.
/// </summary>
public interface IAIAgentRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
