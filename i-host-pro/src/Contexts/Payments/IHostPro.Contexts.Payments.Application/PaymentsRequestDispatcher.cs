using Mediator;

namespace IHostPro.Contexts.Payments.Application;

/// <inheritdoc cref="IPaymentsRequestDispatcher"/>
/// <remarks>
/// Deliberately compiled in THIS SAME assembly/project
/// (<c>Payments.Application</c>) as the <c>Mediator.Mediator</c> type it
/// wraps — see <see cref="IPaymentsRequestDispatcher"/>'s own doc comment.
/// </remarks>
internal sealed class PaymentsRequestDispatcher : IPaymentsRequestDispatcher
{
    private readonly Mediator.Mediator _mediator;

    public PaymentsRequestDispatcher(Mediator.Mediator mediator) => _mediator = mediator;

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _mediator.Send(request, cancellationToken);
}
