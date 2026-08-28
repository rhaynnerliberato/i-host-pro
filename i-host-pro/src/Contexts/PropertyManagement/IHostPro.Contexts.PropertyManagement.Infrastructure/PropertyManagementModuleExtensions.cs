using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Communication;
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
    /// <param name="isDevelopmentEnvironment">
    /// Whether the calling host is running in the Development environment.
    /// Passed explicitly (rather than resolving <c>IHostEnvironment</c>
    /// inside this method) to avoid adding a hosting-abstractions dependency
    /// to this class library — mirrors <c>AddExternalIntegrationsModule</c>'s
    /// own precedent exactly. Gates registration of
    /// <see cref="DevelopmentPropertyAccessCredentialProvider"/> only (Fase
    /// 10, Checkpoint 6.2) — no Production backend exists yet; resolving
    /// <see cref="IPropertyAccessCredentialProvider"/> outside Development
    /// must fail loudly (no registration), never silently succeed.
    /// </param>
    public static IServiceCollection AddPropertyManagementModule(
        this IServiceCollection services, IConfiguration configuration, bool isDevelopmentEnvironment)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — kept identical to
        // PropertyManagementDbContextFactory and
        // IHostPro.MigrationRunner/Program.cs (Architecture Principles §10).
        services.AddDbContext<PropertyManagementDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("PropertyManagement"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management")));

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

        // Fase 10, Checkpoint 4 (Portaria Notification Foundation), ADR-026:
        // the single, minimal synchronous query port Communication may use
        // to resolve a Property's current front desk contact — mirrors
        // IPropertyReservationEligibilityReader's own registration exactly.
        services.AddScoped<IFrontDeskContactReader, FrontDeskContactReader>();

        // Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective
        // Implementation), ADR-028: the single, minimal synchronous query
        // port Communication may use to resolve a Property's guest access
        // credential/instructions for delivery — mirrors
        // IFrontDeskContactReader's own unconditional registration exactly.
        // Registered unconditionally (unlike the credential provider below):
        // if IPropertyAccessCredentialProvider is not registered (any
        // non-Development environment), resolving this reader still
        // succeeds, but actually USING it against an active configuration
        // with a credential reference fails loudly at that point — never a
        // silent Production fallback.
        services.AddScoped<IPropertyGuestAccessReader, PropertyGuestAccessReader>();

        if (isDevelopmentEnvironment)
            services.AddScoped<IPropertyAccessCredentialProvider, DevelopmentPropertyAccessCredentialProvider>();

        return services;
    }
}
