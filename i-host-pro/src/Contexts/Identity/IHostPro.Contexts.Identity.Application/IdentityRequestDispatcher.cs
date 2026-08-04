using Mediator;

namespace IHostPro.Contexts.Identity.Application;

/// <inheritdoc cref="IIdentityRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Identity.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — <see cref="IIdentityRequestDispatcher"/>'s own doc comment
/// explains why: this project's generated <c>Mediator.Mediator</c> is a
/// distinct CLR type from Property Management's, so injecting it directly
/// (never the ambiguous shared <c>Mediator.ISender</c>) is what makes
/// dispatch unambiguous.
/// </remarks>
internal sealed class IdentityRequestDispatcher : IIdentityRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public IdentityRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
