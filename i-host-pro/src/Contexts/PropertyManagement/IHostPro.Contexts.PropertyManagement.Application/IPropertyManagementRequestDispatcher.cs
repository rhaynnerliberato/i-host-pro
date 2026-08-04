using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Application;

/// <summary>
/// This Bounded Context's own request dispatcher (Checkpoint 6 homologação —
/// Mediator <c>ISender</c> cross-context ambiguity fix). Mirrors
/// <c>IIdentityRequestDispatcher</c> exactly — see its own doc comment in
/// <c>Identity.Application</c> for the full root-cause explanation:
/// <c>Mediator.SourceGenerator</c> generates a distinct, assembly-scoped
/// concrete <c>Mediator.Mediator</c> type per project, and the shared
/// <c>Mediator.ISender</c>/<c>IMediator</c>/<c>IPublisher</c> interfaces
/// become ambiguous once both Bounded Contexts register themselves in the
/// same container (only <c>IHostPro.Api</c> does this). This interface
/// exposes only the single overload every real consumer in this codebase
/// actually calls.
/// </summary>
public interface IPropertyManagementRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
