using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Identity;
using IHostPro.Contexts.Identity.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Identity &amp; Access module
/// (Architecture Principles, Section 16) — the Host (IHostPro.Api /
/// IHostPro.Worker) calls this once. Registers the module's DbContext
/// (aliased to the shared, non-generic <see cref="DbContext"/> service so
/// BuildingBlocks' TenantTransactionBehavior/TenantBootstrapBehavior can
/// resolve it generically without knowing IdentityDbContext exists), the
/// custom Identity stores/hasher/validator (Incremento 1 plan, Section 2-3),
/// and the tenant bootstrap reader.
///
/// Custom services are registered <b>before</b> <c>AddIdentityCore</c> is
/// called: ASP.NET Core Identity's registration extensions use
/// <c>TryAdd</c> semantics for the single-instance services
/// (IPasswordHasher/IUserStore/ILookupNormalizer), so registering our
/// implementations first guarantees the framework defaults are never used.
/// </summary>
public static class IdentityModuleExtensions
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — kept identical to
        // IdentityDbContextFactory and IHostPro.MigrationRunner/Program.cs to
        // avoid divergence between design-time, application runtime and
        // migration execution (Architecture Principles, Section 10).
        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Identity"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));

        services.AddScoped<DbContext>(sp => sp.GetRequiredService<IdentityDbContext>());

        services.AddSingleton(TimeProvider.System);

        services.Configure<Argon2Options>(configuration.GetSection(Argon2Options.SectionName));
        services.Configure<PasswordPolicyOptions>(configuration.GetSection(PasswordPolicyOptions.SectionName));

        services.AddScoped<IArgon2idPrimitive, KonsciousArgon2idPrimitive>();
        services.AddScoped<IPasswordHasher<User>, Argon2PasswordHasher>();
        services.AddScoped<IPasswordValidator<User>, PasswordPolicyValidator>();
        services.AddScoped<ILookupNormalizer, EmailLookupNormalizer>();

        services.AddScoped<UserStore>();
        services.AddScoped<IUserStore<User>>(sp => sp.GetRequiredService<UserStore>());
        services.AddScoped<IUserPasswordStore<User>>(sp => sp.GetRequiredService<UserStore>());
        services.AddScoped<IUserEmailStore<User>>(sp => sp.GetRequiredService<UserStore>());
        services.AddScoped<IUserLockoutStore<User>>(sp => sp.GetRequiredService<UserStore>());
        services.AddScoped<IUserSecurityStampStore<User>>(sp => sp.GetRequiredService<UserStore>());

        // RBAC (roles/permissions) is entirely our own model, never ASP.NET
        // Core Identity's — .AddRoles<>() is deliberately not called
        // (Incremento 1 plan). Password/user validators and complexity rules
        // below are disabled on IdentityOptions because they are fully
        // replaced by Argon2PasswordHasher / PasswordPolicyValidator; leaving
        // them enabled would create a second, redundant policy source.
        services.AddIdentityCore<User>(options =>
        {
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 1;

            options.Lockout.AllowedForNewUsers = true;
        });

        // AddDefaultTokenProviders() (password reset / email confirmation
        // tokens) is intentionally not called — no such flow exists in this
        // phase (Incremento 1 plan, Section 12: no full authentication
        // service is in scope yet).

        services.AddScoped<ITenantBootstrapReader, TenantBootstrapReader>();

        return services;
    }
}
