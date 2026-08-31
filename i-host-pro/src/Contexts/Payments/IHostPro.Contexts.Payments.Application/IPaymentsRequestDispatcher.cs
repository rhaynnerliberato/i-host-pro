using Mediator;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// This Bounded Context's own request dispatcher (Fase 11, Checkpoint 3 —
/// the first Mediator consumer Payments ever needed) — mirrors
/// <c>IHousekeepingRequestDispatcher</c>/<c>IReservationsRequestDispatcher</c>
/// exactly: <c>Mediator.SourceGenerator</c> generates a distinct,
/// assembly-scoped concrete <c>Mediator.Mediator</c> type per project, and
/// the shared <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c>
/// interfaces become ambiguous once every Bounded Context registers itself
/// in the same container. This interface exposes only the single overload
/// every real consumer in this codebase actually calls.
/// </summary>
public interface IPaymentsRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
