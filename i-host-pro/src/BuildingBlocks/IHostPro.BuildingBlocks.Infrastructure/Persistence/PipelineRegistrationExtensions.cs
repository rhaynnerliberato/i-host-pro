using IHostPro.BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Registers the tenant-aware transactional boundary's foundation
/// (<see cref="TenantAwareUnitOfWork{TDbContext}"/>) every Bounded Context
/// relies on. Each Host process (IHostPro.Api / IHostPro.Worker) calls this
/// once.
///
/// Registers <see cref="TenantAwareUnitOfWork{TDbContext}"/> as an OPEN
/// generic — safe, unlike <see cref="ValidationBehavior{TMessage,TResponse}"/>/
/// <see cref="TenantBootstrapBehavior{TMessage,TResponse,TDbContext}"/>/
/// <see cref="TenantTransactionBehavior{TMessage,TResponse,TDbContext}"/>
/// (still NOT registered as open generics here, same reason as before —
/// Incremento 2 plan, Etapa 14 correction: an open-generic registration
/// matches EVERY closed type satisfying its constraints, with no way to
/// exclude one, which collided with Identity's Refresh/Logout commands),
/// because DI closes <see cref="TenantAwareUnitOfWork{TDbContext}"/> per
/// <c>TDbContext</c> on demand — there is no ambiguity to exclude anyone
/// from. Each Bounded Context (Identity's <c>AddIdentityCommandDispatch</c>
/// today) registers exactly the pipeline behaviors its own commands/queries
/// need, closed to those specific message types AND to that context's own
/// concrete <see cref="DbContext"/> subclass — never the bare, ambiguous
/// <see cref="DbContext"/> base type (Fase 4 homologation defect: see
/// <see cref="TenantAwareUnitOfWork{TDbContext}"/>'s remarks).
/// </summary>
public static class PipelineRegistrationExtensions
{
    public static IServiceCollection AddIHostProTenantAwarePipeline(this IServiceCollection services)
    {
        services.AddScoped(typeof(TenantAwareUnitOfWork<>));

        return services;
    }
}
