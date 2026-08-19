using Mediator;

namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <inheritdoc cref="IExternalIntegrationsRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>ExternalIntegrations.Application</c>) as the <c>Mediator.Mediator</c>
/// type it wraps — see <see cref="IExternalIntegrationsRequestDispatcher"/>'s
/// own doc comment.
/// </remarks>
internal sealed class ExternalIntegrationsRequestDispatcher : IExternalIntegrationsRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public ExternalIntegrationsRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
