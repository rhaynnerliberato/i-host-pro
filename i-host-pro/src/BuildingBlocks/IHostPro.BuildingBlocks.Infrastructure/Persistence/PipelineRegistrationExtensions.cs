using IHostPro.BuildingBlocks.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Registers the pipeline behaviors every Bounded Context's Commands/Queries go
/// through, in the fixed order the platform requires: validation first (fail
/// fast on malformed input before touching the database), then the
/// tenant-aware transactional boundary — either the bootstrap variant (for
/// <see cref="IBootstrapRequest"/>) or the ordinary one (Architecture
/// Principles, Section 7). Each Host process (IHostPro.Api / IHostPro.Worker)
/// calls this once; individual Bounded Context modules never register these
/// behaviors themselves.
/// </summary>
public static class PipelineRegistrationExtensions
{
    public static IServiceCollection AddIHostProTenantAwarePipeline(this IServiceCollection services)
    {
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantBootstrapBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantTransactionBehavior<,>));
        services.AddScoped<ITenantAwareUnitOfWork, TenantAwareUnitOfWork>();

        return services;
    }
}
