using Mediator;

namespace IHostPro.Contexts.Configuration.Application;

/// <inheritdoc cref="IConfigurationRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Configuration.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="IConfigurationRequestDispatcher"/>'s own doc
/// comment.
/// </remarks>
internal sealed class ConfigurationRequestDispatcher : IConfigurationRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public ConfigurationRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
