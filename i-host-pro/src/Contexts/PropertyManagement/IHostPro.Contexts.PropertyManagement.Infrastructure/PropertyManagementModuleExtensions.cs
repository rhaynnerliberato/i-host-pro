using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Property Management module
/// (Checkpoint 1 plan, item 7; mirrors <c>IdentityModuleExtensions</c>). The
/// Host (IHostPro.Api) calls this once.
/// </summary>
public static class PropertyManagementModuleExtensions
{
    public static IServiceCollection AddPropertyManagementModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — kept identical to
        // PropertyManagementDbContextFactory and
        // IHostPro.MigrationRunner/Program.cs (Architecture Principles §10).
        services.AddDbContext<PropertyManagementDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("PropertyManagement"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management")));

        // Aliased to the shared, non-generic DbContext service (Checkpoint 2)
        // so BuildingBlocks' generic TenantTransactionBehavior/TenantAwareUnitOfWork
        // (used by ListCondominiumsQuery/GetCondominiumDetailQuery) can
        // resolve it without knowing PropertyManagementDbContext exists —
        // mirrors IdentityModuleExtensions' identical aliasing exactly.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<PropertyManagementDbContext>());

        // Checkpoint 2: CreateCondominiumCommandHandler/UpdateCondominiumCommandHandler
        // depend on TimeProvider — mirrors IdentityModuleExtensions'
        // registration exactly.
        services.AddSingleton(TimeProvider.System);

        // Fase 3, Incremento 1 plan, item 4: the single, minimal synchronous
        // query port Reservations may use to check whether a Property is
        // eligible to receive a reservation — mirrors
        // IdentityModuleExtensions' own registration of
        // IIdentityUserEligibilityReader exactly.
        services.AddScoped<IPropertyReservationEligibilityReader, PropertyReservationEligibilityReader>();

        return services;
    }
}
