using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
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

        // Aliased to the shared, non-generic DbContext service so
        // BuildingBlocks' generic TenantTransactionBehavior/TenantAwareUnitOfWork
        // (used by ListReservationsQuery/GetReservationDetailQuery) can
        // resolve it without knowing ReservationsDbContext exists — mirrors
        // PropertyManagementModuleExtensions' identical aliasing exactly.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<ReservationsDbContext>());

        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
