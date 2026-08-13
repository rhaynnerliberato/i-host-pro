using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Infrastructure.Messaging;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Reservations module (Fase 3,
/// Incremento 1 plan) — mirrors <c>PropertyManagementModuleExtensions</c>/
/// <c>IdentityModuleExtensions</c> exactly. The Host (IHostPro.Api) calls
/// this once.
/// </summary>
public static class ReservationsModuleExtensions
{
    public static IServiceCollection AddReservationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors
        // PropertyManagementModuleExtensions/ReservationsDbContextFactory.
        services.AddDbContext<ReservationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Reservations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")));

        services.AddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming Housekeeping's own
    /// Cleaning lifecycle events inside <c>IHostPro.Worker</c> (Fase 7,
    /// Incremento 1 — Agenda Foundation, Checkpoint 1) — deliberately
    /// separate from <see cref="ReservationsCommandDispatchExtensions.AddReservationsCommandDispatch"/>,
    /// which that class's own doc comment restricts to <c>IHostPro.Api</c>'s
    /// composition root only (HTTP command/query dispatch — validators,
    /// pipeline behaviors, Mediator — none of which the Worker process
    /// needs). Registers exactly the services
    /// <see cref="Projections.CleaningScheduleProjectionSynchronizer"/> and
    /// its own dependency chain require, so the tenant-safe execution
    /// boundary (<see cref="IReservationsMessageExecutionScope"/>, ADR-016)
    /// can construct it, from its own child DI scope, for every consumed
    /// Cleaning lifecycle message — mirrors
    /// <c>ReservationsOutboxTransactionExecutor</c>'s registration in
    /// <c>AddReservationsCommandDispatch</c>, duplicated here deliberately
    /// rather than reused, since that method is Api-only by design.
    ///
    /// Checkpoint 1 CLOSURE (real defect found and fixed, ADR-016): the
    /// original design let Wolverine's own codegen resolve
    /// <c>CleaningScheduleProjectionSynchronizer</c> (and transitively
    /// <c>ReservationsDbContext</c>) directly — proven, via a real
    /// generated-chain dispatch and real SQL evidence (<c>WHERE FALSE</c> on
    /// <c>CleaningAssigned</c>'s projection lookup), to materialize
    /// <c>ReservationsDbContext</c> with an <c>ITenantContext</c> instance
    /// different from the one <c>TenantResolutionMiddleware</c> resolved for
    /// that message — the same mechanism ADR-015 documented for
    /// Housekeeping. <see cref="IIntegrationEventHandler{TEvent}"/> mappings
    /// below are resolved exclusively from inside
    /// <see cref="ReservationsMessageExecutionScope"/>'s own child scope,
    /// never from Wolverine's per-message DI resolution.
    /// </summary>
    public static IServiceCollection AddReservationsScheduleProjectionConsumer(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();
        services.AddScoped<CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningCreated>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningAssigned>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningInTransit>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningStarted>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningInspectionStarted>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningCompleted>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningInterrupted>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningNeedsHelp>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningNeedsMaterial>, CleaningScheduleProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<CleaningCancelled>, CleaningScheduleProjectionSynchronizer>();

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }
}
