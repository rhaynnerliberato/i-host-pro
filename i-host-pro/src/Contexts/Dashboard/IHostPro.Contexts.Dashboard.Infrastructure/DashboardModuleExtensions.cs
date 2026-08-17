using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Dashboard.Application;
using IHostPro.Contexts.Dashboard.Infrastructure.Messaging;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Dashboard.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Dashboard &amp; Reporting
/// module's shared foundation (Fase 7, Incremento 2) — DbContext
/// registration and <c>TimeProvider</c>, mirrors
/// <c>ReservationsModuleExtensions.AddReservationsModule</c> exactly. Called
/// by BOTH <c>IHostPro.Api</c> (Checkpoint 2's Overview query needs
/// <see cref="DashboardDbContext"/>) and <c>IHostPro.Worker</c> (the
/// projection synchronizers also need it) — the event-consumer-only slice
/// lives in <see cref="AddDashboardProjectionConsumer"/> instead, called
/// ONLY by <c>IHostPro.Worker</c> (mirrors
/// <c>AddReservationsScheduleProjectionConsumer</c>'s own split exactly).
/// </summary>
public static class DashboardModuleExtensions
{
    public static IServiceCollection AddDashboardModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors every other
        // context's own module extensions.
        services.AddDbContext<DashboardDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Dashboard"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard")));

        services.AddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// The event-consumer slice (Fase 7, Incremento 2, Checkpoint 1) — the
    /// four projection synchronizers' full DI graph, so the tenant-safe
    /// execution boundary (<see cref="IDashboardMessageExecutionScope"/>,
    /// ADR-016) can construct each, from its own child DI scope, for every
    /// consumed event. Deliberately separate from <see cref="AddDashboardModule"/>
    /// — mirrors <c>AddReservationsScheduleProjectionConsumer</c>'s own doc
    /// comment: <c>IHostPro.Api</c> only publishes Integration Events, it
    /// never consumes messages (Architecture Principles, Section 2), so it
    /// must never register these handlers.
    /// </summary>
    public static IServiceCollection AddDashboardProjectionConsumer(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IDashboardTransactionExecutor, DashboardOutboxTransactionExecutor>();

        // Keyed (Fase 7, Incremento 2, Checkpoint 1 — real-Worker regression
        // found and fixed): IIntegrationEventHandler<T> is a shared generic
        // interface, and most of these exact event types are ALSO already
        // consumed by Housekeeping and/or Reservations in the same
        // IHostPro.Worker DI container. GetRequiredService<T>() for a type
        // with multiple registrations silently returns the LAST one
        // registered, which — since this module is registered last — was
        // shadowing Housekeeping's/Reservations' own handler resolution too.
        // See DashboardMessageExecutionScope.
        services.AddScoped<DashboardReservationProjectionSynchronizer>();
        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, DashboardReservationProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<ReservationUpdated>, DashboardReservationProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCancelled>, DashboardReservationProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);

        services.AddScoped<DashboardCleaningProjectionSynchronizer>();
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCreated>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningAssigned>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInTransit>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningStarted>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInspectionStarted>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCompleted>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInterrupted>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsHelp>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsMaterial>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCancelled>, DashboardCleaningProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);

        services.AddScoped<DashboardPropertyProjectionSynchronizer>();
        services.AddKeyedScoped<IIntegrationEventHandler<PropertyCreated>, DashboardPropertyProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<PropertyActivated>, DashboardPropertyProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<PropertyDeactivated>, DashboardPropertyProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<PropertyArchived>, DashboardPropertyProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);

        services.AddScoped<DashboardOccurrenceProjectionSynchronizer>();
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningOccurrenceRegistered>, DashboardOccurrenceProjectionSynchronizer>(
            DashboardMessageExecutionScope.HandlerKey);

        services.AddScoped<IDashboardMessageExecutionScope, DashboardMessageExecutionScope>();

        return services;
    }
}
