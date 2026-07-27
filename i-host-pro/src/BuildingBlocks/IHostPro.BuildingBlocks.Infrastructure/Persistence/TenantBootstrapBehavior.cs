using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Mediator;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Orchestrates the full transactional boundary for an
/// <see cref="IBootstrapRequest"/>: resolves the tenant via the request's own
/// <see cref="ITenantBootstrapResolver{TRequest}"/> (touching only the minimal,
/// non-tenant-owned data needed to identify the tenant), sets
/// <see cref="ITenantContext"/>, then delegates to the exact same
/// <see cref="ITenantAwareUnitOfWork"/> primitive
/// <see cref="TenantTransactionBehavior{TMessage,TResponse}"/> uses for every
/// other request. No application code runs between tenant resolution and the
/// RLS-protected transaction being open — the handler itself never manages a
/// transactional boundary differently from any other Command/Query handler.
///
/// <para>
/// RESERVED EXCLUSIVELY FOR AUTHENTICATION BOOTSTRAP (login, refresh token
/// exchange). This mechanism exists only because those two use cases must run
/// before the caller is authenticated, so no trustworthy tenant claim exists
/// yet. It is <b>not</b> a general-purpose alternative to
/// <see cref="TenantTransactionBehavior{TMessage,TResponse}"/>. Any other use
/// case that appears to need this pattern (resolving tenant from something
/// other than an authenticated claim) requires a new architectural decision
/// (Decision Making Policy, Category B) before implementing an
/// <see cref="IBootstrapRequest"/> for it — do not reuse this mechanism by
/// convenience alone.
/// </para>
/// </summary>
public sealed class TenantBootstrapBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage, IBootstrapRequest
{
    private readonly ITenantBootstrapResolver<TMessage> _tenantResolver;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantAwareUnitOfWork _unitOfWork;

    public TenantBootstrapBehavior(
        ITenantBootstrapResolver<TMessage> tenantResolver,
        ITenantContext tenantContext,
        ITenantAwareUnitOfWork unitOfWork)
    {
        _tenantResolver = tenantResolver;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var tenantId = await _tenantResolver.ResolveTenantAsync(message, cancellationToken);

        if (tenantId is null)
        {
            return GenericResultFailureFactory.Create<TResponse>(
                new Error("Tenant.NotFound", "The tenant could not be resolved."));
        }

        _tenantContext.SetTenant(tenantId.Value);

        var readOnly = message is IReadOnlyRequest;

        return await _unitOfWork.ExecuteAsync(
            readOnly,
            () => next(message, cancellationToken).AsTask(),
            cancellationToken);
    }
}
