using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;

namespace IHostPro.Contexts.Configuration.Infrastructure.Messaging;

/// <summary>
/// The sole consumer of <c>PolicyUpdated</c> in this increment (Fase 5,
/// Incremento 1, Checkpoint 6, §10: "consumidor inicial do evento:
/// invalidação de cache dentro do próprio contexto" — never a consumer in
/// Reservations or any other Bounded Context). Contains no Wolverine
/// reference at all (Architecture Principles §11) — <see cref="PolicyUpdatedHandler"/>
/// is the minimal, mechanical adapter that actually resolves and invokes
/// this from the messaging library. Runs exclusively in
/// <c>IHostPro.Worker</c>.
///
/// Checkpoint 7 homologação (Fase 5), real defect found and fixed: this
/// class used to be named <c>PolicyUpdatedCacheInvalidationHandler</c> —
/// Wolverine's own convention-based handler discovery (class name ending in
/// "Handler", public method starting with "Handle") matched it directly, in
/// addition to matching the intended <see cref="PolicyUpdatedHandler"/>
/// adapter, so Wolverine merged BOTH into one generated method for
/// <c>PolicyUpdated</c> and produced two conflicting local-variable
/// declarations for the same type (<c>CS0128</c>) — confirmed by direct
/// observation, the first time <c>opts.Discovery.IncludeAssembly</c> ever
/// scanned this assembly for real. Dropping the "Handler" suffix removes it
/// from Wolverine's discovery entirely, leaving <see cref="PolicyUpdatedHandler"/>
/// as the only thing Wolverine ever finds for this message — exactly the
/// original design intent.
/// </summary>
public sealed class PolicyUpdatedCacheInvalidation : IIntegrationEventHandler<PolicyUpdated>
{
    private readonly IPolicyCacheInvalidator _cacheInvalidator;

    public PolicyUpdatedCacheInvalidation(IPolicyCacheInvalidator cacheInvalidator) => _cacheInvalidator = cacheInvalidator;

    public Task HandleAsync(PolicyUpdated @event, CancellationToken cancellationToken = default) =>
        _cacheInvalidator.InvalidateAsync(@event.TenantId, @event.PolicyCode, cancellationToken);
}
