using IHostPro.BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Registers the tenant-aware transactional boundary's foundation
/// (<see cref="ITenantAwareUnitOfWork"/>) every Bounded Context relies on.
/// Each Host process (IHostPro.Api / IHostPro.Worker) calls this once.
///
/// Does NOT register <see cref="ValidationBehavior{TMessage,TResponse}"/>/
/// <see cref="TenantBootstrapBehavior{TMessage,TResponse}"/>/
/// <see cref="TenantTransactionBehavior{TMessage,TResponse}"/> as open
/// generics anymore (Incremento 2 plan, Etapa 14 correction): an open-generic
/// registration matches EVERY closed type satisfying its constraints, with no
/// way to exclude one — this collided with Identity's Refresh/Logout
/// commands, which need a DIFFERENT tenant-aware step
/// (<c>RefreshTokenTenantAwareBehavior</c>/<c>LogoutTenantAwareBehavior</c>,
/// wrapping <c>IRefreshTokenExchangeExecutor</c>/<c>ILogoutExecutor</c>'s
/// concurrency retry) instead of the plain generic ones, causing both to run
/// and each independently open a Unit of Work
/// (<c>NestedUnitOfWorkException</c>). These three behaviors were registered
/// here from Incremento 1 but never actually exercised until Etapa 14 wired
/// real Mediator dispatch — no test or running host relied on Mediator's own
/// pipeline-behavior resolution before now (every earlier test manually
/// replicated validation -&gt; tenant resolution -&gt; Unit of Work -&gt;
/// handler, calling each step directly), so removing them here is safe.
/// Each Bounded Context (Identity's <c>AddIdentityCommandDispatch</c> today)
/// now registers exactly the pipeline behaviors its own commands need,
/// closed to those specific message types where a specialized behavior
/// exists.
/// </summary>
public static class PipelineRegistrationExtensions
{
    public static IServiceCollection AddIHostProTenantAwarePipeline(this IServiceCollection services)
    {
        services.AddScoped<ITenantAwareUnitOfWork, TenantAwareUnitOfWork>();

        return services;
    }
}
