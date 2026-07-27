using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Mediator;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Wraps every ordinary Command/Query in a tenant-aware transaction
/// (<see cref="ITenantAwareUnitOfWork"/>). Requires <see cref="ITenantContext"/>
/// to already be resolved (from an authenticated JWT claim in IHostPro.Api or
/// from the message envelope in IHostPro.Worker) — this is the fail-closed
/// default every request goes through.
///
/// Requests implementing <c>IBootstrapRequest</c> (login, refresh) are skipped
/// here: they have no trustworthy tenant yet and are instead wrapped by
/// <see cref="TenantBootstrapBehavior{TMessage,TResponse}"/>, which resolves the
/// tenant first and then delegates to the same <see cref="ITenantAwareUnitOfWork"/>
/// primitive used here (Architecture Principles, Section 7).
/// </summary>
public sealed class TenantTransactionBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    private readonly ITenantAwareUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public TenantTransactionBehavior(ITenantAwareUnitOfWork unitOfWork, ITenantContext tenantContext)
    {
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        // Bootstrap requests own their own transactional boundary via
        // TenantBootstrapBehavior — this behavior must never also wrap them, or
        // the DbContext would be asked to open a second, nested transaction for
        // the same request (rejected by NestedUnitOfWorkException).
        if (message is IBootstrapRequest)
            return await next(message, cancellationToken);

        if (!_tenantContext.IsResolved)
            throw new TenantContextNotResolvedException();

        var readOnly = message is IReadOnlyRequest;

        return await _unitOfWork.ExecuteAsync(
            readOnly,
            () => next(message, cancellationToken).AsTask(),
            cancellationToken);
    }
}
