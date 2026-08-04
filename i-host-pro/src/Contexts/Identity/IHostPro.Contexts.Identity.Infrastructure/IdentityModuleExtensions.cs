using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Authorization;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Catalog;
using IHostPro.Contexts.Identity.Infrastructure.Identity;
using IHostPro.Contexts.Identity.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using IHostPro.Contexts.Identity.Infrastructure.Sessions;
using IHostPro.Contexts.Identity.Infrastructure.Users;
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

        // Permission resolver (Incremento 3, Checkpoint 2) — RolePermissionCache
        // is a Singleton (the actual cross-request cache storage); PermissionReader
        // is Scoped, matching IdentityDbContext's own lifetime. Registered for
        // both hosts: the reader itself has no HTTP dependency (only
        // IdentityDbContext, already shared), mirroring IUserRoleReader/
        // IRefreshTokenReader above — only PermissionAuthorizationHandler,
        // which consumes it, is Api-only (AddIdentityAuthorization,
        // Identity.Api).
        services.AddOptions<PermissionCacheOptions>()
            .Bind(configuration.GetSection(PermissionCacheOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PermissionCacheOptions>, PermissionCacheOptionsValidator>();
        services.AddSingleton<RolePermissionCache>();
        services.AddScoped<IPermissionReader, PermissionReader>();

        // Administrative catalog listing (Incremento 3, Checkpoint 3) —
        // GET /api/v1/roles and GET /api/v1/permissions. Deliberately NOT
        // IPermissionReader above: that one resolves permissions for
        // authorization and may cache; this one lists the whole catalog for
        // an administrative client and always re-reads PostgreSQL directly
        // (no cache introduced here). Registered for both hosts — the reader
        // itself has no HTTP dependency, mirroring IPermissionReader.
        services.AddScoped<IIdentityCatalogReader, IdentityCatalogReader>();

        // Self-service profile/session listing (Incremento 3, Checkpoint 4) —
        // GET /api/v1/users/me and GET /api/v1/users/me/sessions.
        // GetOwnProfileQueryHandler needs no new reader (reuses
        // IUserAuthenticationService/IUserRoleReader below); ISessionReader is
        // the one genuinely new read capability this checkpoint adds — a
        // plain, uncached list-by-criteria query, registered for both hosts
        // like every other reader in this module (no HTTP dependency of its
        // own).
        services.AddScoped<ISessionReader, SessionReader>();

        // Administrative user creation/listing/detail (Incremento 3,
        // Checkpoint 5) — POST /api/v1/users, GET /api/v1/users, GET
        // /api/v1/users/{userId}. IUserProvisioningService wraps
        // IPasswordValidator<User>/IPasswordHasher<User> directly (never
        // UserManager<User>.CreateAsync — see its own doc comment for why).
        // IRepository<User,Guid> is the first write path for User itself
        // (Login/Refresh only ever read one, via IUserAuthenticationService).
        // IUserRoleWriter is UserRole's write-side counterpart to
        // IUserRoleReader — UserRole is not an AggregateRoot, so it cannot use
        // the generic IRepository<,>. All registered for both hosts, mirroring
        // every other reader/writer in this module (no HTTP dependency of
        // their own).
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<IRepository<User, Guid>, UserRepository>();
        services.AddScoped<IUserRoleWriter, UserRoleWriter>();
        services.AddScoped<IUserAdministrationReader, UserAdministrationReader>();

        services.AddOptions<UserListingOptions>()
            .Bind(configuration.GetSection(UserListingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<UserListingOptions>, UserListingOptionsValidator>();
        services.AddScoped<IUserListingSettingsProvider, UserListingSettingsProvider>();

        // Role assignment/removal (Incremento 3, Checkpoint 6) — POST/DELETE
        // /api/v1/users/{userId}/roles(/{roleCode}). ILastAdministratorGuard
        // is consulted only when removing ADMIN specifically; its PostgreSQL
        // advisory-lock mechanics live entirely in the Infrastructure
        // implementation (Application stays framework-neutral).
        // IUserSessionRevoker generalizes RevokeOwnSessionCommandHandler's own
        // single-session cascade to every active session of a user — reused
        // as-is by both AssignRole/RemoveRole and, per Section 7, intended for
        // reuse by the block/password-change checkpoints still to come. Both
        // registered for both hosts, mirroring every other reader/writer in
        // this module (no HTTP dependency of their own).
        services.AddScoped<ILastAdministratorGuard, LastAdministratorGuard>();
        services.AddScoped<IUserSessionRevoker, UserSessionRevoker>();

        // Property Management's Ownership eligibility check (Fase 2,
        // Incremento 1, Checkpoint 5 plan, item 5) — the one synchronous
        // cross-context query Property Management is authorized to make.
        // Registered for both hosts, mirroring every other reader in this
        // module (no HTTP dependency of its own); Property Management may
        // depend only on the Identity.Contracts interface, never this
        // concrete Infrastructure type.
        services.AddScoped<IIdentityUserEligibilityReader, IdentityUserEligibilityReader>();

        // LoginCommandHandler/RefreshTokenCommandHandler/LogoutCommandHandler/
        // ListRolesQueryHandler/ListPermissionsQueryHandler/
        // GetOwnProfileQueryHandler/ListOwnSessionsQueryHandler/
        // RevokeOwnSessionCommandHandler/CreateUserCommandHandler/
        // ListUsersQueryHandler/GetUserByIdQueryHandler/AssignRoleCommandHandler/
        // RemoveRoleCommandHandler/BlockUserCommandHandler/
        // UnblockUserCommandHandler/UpdateUserCommandHandler/
        // ChangeOwnPasswordCommandHandler/AdminResetPasswordCommandHandler are
        // not registered here, concrete type included: AddIdentityApplicationMediator()
        // (called from AddIdentityCommandDispatch, Api-only — dispatching a
        // Command/Query is an HTTP-request concern) discovers and registers
        // every handler in the Application assembly automatically. Each is
        // also registered ad hoc by the unit tests that exercise it directly
        // (see LoginCommandHandlerTests/RefreshTokenCommandHandlerTests/
        // LogoutCommandHandlerTests/ListRolesQueryHandlerTests/
        // ListPermissionsQueryHandlerTests/GetOwnProfileQueryHandlerTests/
        // ListOwnSessionsQueryHandlerTests/RevokeOwnSessionCommandHandlerTests/
        // CreateUserCommandHandlerTests/ListUsersQueryHandlerTests/
        // GetUserByIdQueryHandlerTests/AssignRoleCommandHandlerTests/
        // RemoveRoleCommandHandlerTests/BlockUserCommandHandlerTests/
        // UnblockUserCommandHandlerTests/UpdateUserCommandHandlerTests/
        // ChangeOwnPasswordCommandHandlerTests/AdminResetPasswordCommandHandlerTests).

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
