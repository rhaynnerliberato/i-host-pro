using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Application.Overview;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Dashboard.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Dashboard's own
/// Commands/Queries — mirrors <c>ReservationsCommandDispatchExtensions</c>
/// exactly. Called ONLY from <c>IHostPro.Api</c>'s composition root, never
/// from <c>IHostPro.Worker</c>'s: dispatching a Command/Query is an
/// HTTP-request concern.
///
/// <see cref="GetDashboardOverviewQuery"/> is Checkpoint 2's only
/// Command/Query — a plain read, no event, no outbox — registered with the
/// shared, generic <c>TenantTransactionBehavior&lt;,,&gt;</c> directly.
/// </summary>
public static class DashboardQueryDispatchExtensions
{
    public static IServiceCollection AddDashboardQueryDispatch(this IServiceCollection services)
    {
        services.AddDashboardApplicationMediator();

        services.AddScoped<IValidator<GetDashboardOverviewQuery>, GetDashboardOverviewQueryValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, it has no tenant/transaction side effects to collide
        // with — mirrors AddReservationsCommandDispatch exactly.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IDashboardOverviewReader, DashboardOverviewReader>();

        // Closed to DashboardDbContext explicitly (Fase 4 homologation fix
        // precedent) — never the ambiguous, unparameterized DbContext base
        // type.
        services.AddScoped<
            IPipelineBehavior<GetDashboardOverviewQuery, Result<DashboardOverviewResult>>,
            TenantTransactionBehavior<GetDashboardOverviewQuery, Result<DashboardOverviewResult>, DashboardDbContext>>();

        return services;
    }
}
