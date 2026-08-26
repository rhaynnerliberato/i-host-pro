using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.GuestOperations.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Guest Operations module
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation) — mirrors
/// <c>ReservationsModuleExtensions</c> exactly. This checkpoint has zero
/// public API endpoints, so there is no separate "command dispatch" method
/// (no Mediator, no validators, no pipeline behaviors) — everything
/// <see cref="RecordGuestCheckedOutCommandHandler"/> needs to be resolved
/// directly (by <c>IHostPro.Api</c>'s own composition root, the only process
/// that invokes it this checkpoint) is registered by
/// <see cref="AddGuestOperationsModule"/> itself.
/// </summary>
public static class GuestOperationsModuleExtensions
{
    public static IServiceCollection AddGuestOperationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors every other module's
        // own registration.
        services.AddDbContext<GuestOperationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("GuestOperations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations")));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IGuestOperationsTransactionExecutor, GuestOperationsOutboxTransactionExecutor>();
        services.AddScoped<IRepository<GuestStayOperation, Guid>, GuestStayOperationRepository>();
        services.AddScoped<IGuestStayOperationReader, GuestStayOperationReader>();
        services.AddScoped<IRecordGuestCheckedOutHandler, RecordGuestCheckedOutCommandHandler>();

        return services;
    }
}
