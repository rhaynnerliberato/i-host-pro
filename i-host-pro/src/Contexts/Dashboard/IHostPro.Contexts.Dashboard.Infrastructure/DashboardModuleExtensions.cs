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
/// module's event-consumer slice (Fase 7, Incremento 2, Checkpoint 1) —
/// mirrors <c>ReservationsModuleExtensions.AddReservationsScheduleProjectionConsumer</c>
/// exactly: this Bounded Context has no HTTP command/query dispatch this
/// increment (Overview API is Checkpoint 2), so there is no separate
/// "module" vs. "consumer" split yet — everything Dashboard needs lives in
/// this single method. <c>IHostPro.Worker</c> calls this once.
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
