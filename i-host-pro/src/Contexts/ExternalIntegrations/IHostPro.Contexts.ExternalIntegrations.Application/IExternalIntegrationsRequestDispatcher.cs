using Mediator;

namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// This Bounded Context's own request dispatcher — mirrors the equivalent
/// per-context dispatcher interface already used by every other Bounded
/// Context (Reservations, Property Management, Identity, Configuration &amp;
/// Policy) exactly (Fase 2, Checkpoint 6 homologação — Mediator
/// <c>ISender</c> cross-context ambiguity fix): <c>Mediator.SourceGenerator</c>
/// generates a distinct, assembly-scoped concrete <c>Mediator.Mediator</c>
/// type per project, and the shared <c>Mediator.ISender</c>/<c>IMediator</c>/
/// <c>IPublisher</c> interfaces become ambiguous once every Bounded Context
/// registers itself in the same container (only <c>IHostPro.Api</c> does
/// this). This interface exposes only the single overload every real
/// consumer in this codebase actually calls.
/// </summary>
public interface IExternalIntegrationsRequestDispatcher
{
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
