using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Infrastructure.Communication;
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

        // Fase 9, Checkpoint 1 — ADR-019: the single, purpose-limited
        // synchronous query port Communication may use to read a guest's
        // contact data for one Reservation. Registered here (not only in
        // IHostPro.Api) because Communication's own Wolverine consumer runs
        // in IHostPro.Worker, and AddReservationsModule already runs in both
        // processes — mirrors PropertyManagementModuleExtensions' own
        // registration of IPropertyReservationEligibilityReader (ADR-014).
        services.AddScoped<IReservationGuestContactReader, ReservationGuestContactReader>();

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

        // Keyed (Fase 7, Incremento 2, Checkpoint 1 — real-Worker regression
        // found and fixed): IIntegrationEventHandler<T> is a shared generic
        // interface, and Dashboard now ALSO registers handlers for these
        // exact ten Cleaning lifecycle event types in the same
        // IHostPro.Worker DI container. GetRequiredService<T>() for a type
        // with multiple registrations silently returns the LAST one
        // registered — an unkeyed registration here would let Dashboard's
        // own module (registered after this one) shadow Reservations' own
        // handler resolution. See ReservationsMessageExecutionScope.
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCreated>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningAssigned>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInTransit>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningStarted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInspectionStarted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCompleted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInterrupted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsHelp>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsMaterial>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCancelled>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }
}
