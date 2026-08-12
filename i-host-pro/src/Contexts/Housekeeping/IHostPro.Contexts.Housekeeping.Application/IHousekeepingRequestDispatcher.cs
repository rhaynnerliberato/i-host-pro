using Mediator;

namespace IHostPro.Contexts.Housekeeping.Application;

/// <summary>
/// This Bounded Context's own request dispatcher — mirrors
/// <c>IReservationsRequestDispatcher</c>/<c>IPropertyManagementRequestDispatcher</c>/
/// <c>IIdentityRequestDispatcher</c> exactly (Checkpoint 6 homologação, Fase
/// 2 — Mediator <c>ISender</c> cross-context ambiguity fix):
/// <c>Mediator.SourceGenerator</c> generates a distinct, assembly-scoped
/// concrete <c>Mediator.Mediator</c> type per project, and the shared
/// <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c> interfaces
/// become ambiguous once every Bounded Context registers itself in the same
/// container (only <c>IHostPro.Api</c> does this). This interface exposes
/// only the single overload every real consumer in this codebase actually
/// calls.
/// </summary>
public interface IHousekeepingRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
