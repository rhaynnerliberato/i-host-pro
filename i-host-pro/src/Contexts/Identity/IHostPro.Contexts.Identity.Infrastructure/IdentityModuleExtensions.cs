using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Identity;
using IHostPro.Contexts.Identity.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// <param name="isDevelopmentEnvironment">
    /// Whether the calling host is running in the Development environment.
    /// Passed explicitly (rather than resolving <c>IHostEnvironment</c> inside
    /// this method) to avoid adding a hosting-abstractions dependency to this
    /// class library — the Host already knows its own environment
    /// (<c>builder.Environment.IsDevelopment()</c>). Gates registration of
    /// <see cref="DevelopmentSeedOptions"/> (Incremento 2 plan, ajuste 3-4):
    /// outside Development it is never bound, never validated and has no
    /// effect, regardless of configuration content.
    /// </param>
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopmentEnvironment)
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

        // Eagerly validated at startup (ValidateOnStart) — unlike Argon2Options/
        // PasswordPolicyOptions above, these two carry explicit min/max bounds
        // (Incremento 2 plan, ajuste 5) because they govern token lifetime and
        // rotation-concurrency behavior directly reachable from an HTTP request.
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RefreshTokenOptions>, RefreshTokenOptionsValidator>();

        // Refresh token generation/parsing/hashing hold no secret material
        // (unlike the JWT signing key — Etapa 6) — RandomNumberGenerator is
        // stateless/OS-provided and RefreshTokenOptions is not sensitive, so
        // these are registered here for both hosts, not restricted to
        // IHostPro.Api the way AddIdentityJwtIssuance is.
        services.AddSingleton<IRefreshTokenHasher, RefreshTokenHasher>();
        services.AddSingleton<IRefreshTokenParser, RefreshTokenParser>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();

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
        //
        // options.Lockout is deliberately left untouched here — until
        // Incremento 2 (Etapa 9) MaxFailedAccessAttempts/DefaultLockoutTimeSpan
        // were silently the ASP.NET Core Identity framework defaults, never an
        // explicit project decision. AccountLockoutOptions below (validated,
        // ValidateOnStart) now applies all three Lockout values explicitly via
        // PostConfigure, which runs after this delegate regardless of
        // registration order.
        services.AddIdentityCore<User>(options =>
        {
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 1;
        });

        services.AddOptions<AccountLockoutOptions>()
            .Bind(configuration.GetSection(AccountLockoutOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AccountLockoutOptions>, AccountLockoutOptionsValidator>();

        services.AddOptions<IdentityOptions>()
            .PostConfigure<IOptions<AccountLockoutOptions>>((identityOptions, lockoutOptions) =>
            {
                var lockout = lockoutOptions.Value;
                identityOptions.Lockout.MaxFailedAccessAttempts = lockout.MaxFailedAccessAttempts;
                identityOptions.Lockout.DefaultLockoutTimeSpan = lockout.DefaultLockoutDuration;
                identityOptions.Lockout.AllowedForNewUsers = lockout.AllowedForNewUsers;
            });

        // AddDefaultTokenProviders() (password reset / email confirmation
        // tokens) is intentionally not called — no such flow exists in this
        // phase (Incremento 1 plan, Section 12: no full authentication
        // service is in scope yet).

        services.AddScoped<ITenantBootstrapReader, TenantBootstrapReader>();

        // Login use case (Incremento 2 plan, Etapa 9). None of these hold
        // secret material (the JWT signing key is the only exception,
        // Etapa 6, and stays Api-only via AddIdentityJwtIssuance) — shared
        // for both hosts, consistent with the rest of this module.
        services.AddScoped<IUserAuthenticationService, UserAuthenticationService>();
        services.AddScoped<IUserRoleReader, UserRoleReader>();
        services.AddScoped<ISecurityAuditWriter, SecurityAuditWriter>();
        services.AddSingleton<IDummyPasswordVerifier, DummyPasswordVerifier>();
        services.AddScoped<IRepository<Session, Guid>, SessionRepository>();
        services.AddScoped<IRepository<RefreshToken, Guid>, RefreshTokenRepository>();
        services.AddScoped<ITenantBootstrapResolver<LoginCommand>, LoginTenantBootstrapResolver>();

        // Refresh token exchange (Incremento 2 plan, Etapa 10).
        services.AddScoped<IRefreshTokenReader, RefreshTokenReader>();
        services.AddSingleton<IRefreshTokenRotationPolicy, RefreshTokenRotationPolicy>();
        services.AddScoped<ITenantBootstrapResolver<RefreshTokenCommand>, RefreshTokenTenantBootstrapResolver>();

        // IRefreshTokenExchangeExecutor/ILogoutExecutor (local, use-case-specific
        // DbUpdateConcurrencyException retry) are NOT registered here — since
        // Etapa 15A their implementations depend on IIdentityTransactionExecutor,
        // which is Api-only (it needs IDbContextOutbox<IdentityDbContext>, which
        // only exists once IHostPro.Api's Wolverine host enrolls Identity's
        // outbox — see AddIdentityCommandDispatch, where both are registered
        // together, alongside the behaviors that are their only callers).

        // Session-revocation cache acceleration (Incremento 2 plan, Etapa
        // 12). ISessionRevocationSignal is a plain in-memory per-request
        // collector, safe for both hosts. ISessionRevocationCache defaults to
        // a silent no-op here — IHostPro.Api overrides it with the real
        // Redis-backed implementation via AddIdentitySessionRevocationCache;
        // IHostPro.Worker never does, by design ("registro somente no host
        // API"), and stays fully DI-valid regardless.
        services.AddScoped<ISessionRevocationSignal, SessionRevocationSignal>();
        services.AddScoped<ISessionRevocationCache, NullSessionRevocationCache>();

        // LoginCommandHandler/RefreshTokenCommandHandler/LogoutCommandHandler
        // (Logout: Incremento 2 plan, Etapa 11 — IRepository<Session, Guid>,
        // IRefreshTokenReader and ISecurityAuditWriter above already cover
        // everything it depends on, no new registrations needed for it)
        // are not registered here at all, concrete type included: no
        // AddMediator() call exists anywhere in this solution yet (confirmed
        // by inspection) — wiring them is a dependency of the future
        // endpoints step, which is the first point something actually needs
        // to call IMediator/ISender.Send(...). Each is registered ad hoc by
        // the tests that exercise it directly in the meantime (see
        // LoginCommandHandlerTests/RefreshTokenCommandHandlerTests/
        // LogoutCommandHandlerTests).

        // Development-only tenant/user bootstrap data (Incremento 2 plan,
        // ajuste 3-4). Registered — and therefore bound/validated, and the
        // seeder itself registered as a hosted service — exclusively in
        // Development: outside it, neither DevelopmentSeedOptions nor
        // DevelopmentIdentitySeeder exist in the container at all, not merely
        // disabled by a flag. DevelopmentIdentitySeeder no-ops at startup when
        // DevelopmentSeedOptions.Enabled is false (the default), and is safe
        // to register in both IHostPro.Api and IHostPro.Worker (both call
        // AddIdentityModule) — a PostgreSQL advisory lock, not in-process
        // coordination, is what makes concurrent instances safe.
        if (isDevelopmentEnvironment)
        {
            services.AddOptions<DevelopmentSeedOptions>()
                .Bind(configuration.GetSection(DevelopmentSeedOptions.SectionName))
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<DevelopmentSeedOptions>, DevelopmentSeedOptionsValidator>();
            services.AddHostedService<DevelopmentIdentitySeeder>();
        }

        return services;
    }
}
