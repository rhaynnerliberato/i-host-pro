using Mediator;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// This Bounded Context's own request dispatcher — mirrors
/// <c>IPaymentsRequestDispatcher</c>/<c>IReservationsRequestDispatcher</c>
/// exactly: <c>Mediator.SourceGenerator</c> generates a distinct,
/// assembly-scoped concrete <c>Mediator.Mediator</c> type per project, and
/// the shared <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c>
/// interfaces become ambiguous once every Bounded Context registers itself
/// in the same container (the Worker composes all of them). This interface
/// exposes only the single overload every real consumer in this codebase
/// actually calls.
/// </summary>
public interface ICommunicationRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
