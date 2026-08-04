using Mediator;

namespace IHostPro.Contexts.PropertyManagement.Application;

/// <inheritdoc cref="IPropertyManagementRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>PropertyManagement.Application</c>) as the <c>Mediator.Mediator</c>
/// type it wraps — see <see cref="IPropertyManagementRequestDispatcher"/>'s
/// own doc comment.
/// </remarks>
internal sealed class PropertyManagementRequestDispatcher : IPropertyManagementRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public PropertyManagementRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
