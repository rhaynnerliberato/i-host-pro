using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of the session-revocation cache acceleration against
/// real PostgreSQL and real Redis instances (Incremento 2 plan, Etapa 12):
/// confirms Logout and Refresh's reuse-outside-grace-window path each write
/// the documented key/TTL to Redis only after their transaction commits,
/// that a Redis outage never fails either use case, and that nothing
/// sensitive is ever stored.
/// </summary>
public class SessionRevocationCacheTests : IClassFixture<SessionRevocationCacheTests.Fixture>, IDisposable
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownSecret = "known-secret-segment-for-tests";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly RedisContainer _redisContainer;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    // Lazily created by ConnectToRedis(), disposed by Dispose() below — fixes
    // a real test-lifecycle defect (Incremento 3, Checkpoint 2 stabilization):
    // every one of the seven call sites of ConnectToRedis() previously created
    // its own ConnectionMultiplexer.Connect(...) and never disposed it. xUnit
    // constructs a new SessionRevocationCacheTests instance per [Fact] (see
    // the Fixture doc comment below), so this was never a single leak but one
    // per affected test method — each leaked multiplexer keeps its own TCP
    // connection and background reconnect/heartbeat threads alive against the
    // one Redis container this whole class shares via IClassFixture for the
    // remainder of the test run. Reproduced and confirmed as the cause of the
    // intermittent `ObjectDisposedException` on `PhysicalConnection.ReadAllAsync`
    // (StackExchange.Redis's own internal socket teardown racing an in-flight
    // read once enough accumulated background activity destabilized the
    // connection) — never a production code defect; RedisSessionRevocationCache
    // itself already manages its own connection correctly and was not changed.
    private ConnectionMultiplexer? _redisMultiplexer;

    public SessionRevocationCacheTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    public void Dispose() => _redisMultiplexer?.Dispose();

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// Also provisions Identity's outbox (mirrors <c>IHostPro.MigrationRunner</c>'s
    /// Etapa 15A block) since <see cref="ILogoutExecutor"/>/<see cref="IRefreshTokenExchangeExecutor"/>
    /// depend on <see cref="IIdentityTransactionExecutor"/>, which needs
    /// <c>IDbContextOutbox&lt;IdentityDbContext&gt;</c>.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public RedisContainer RedisContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            RedisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();

            await Task.WhenAll(_postgresContainer.StartAsync(), RedisContainer.StartAsync());

            var adminConnectionString = _postgresContainer.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await ExecuteAsync(adminConnection, $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """);
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
            await RedisContainer.DisposeAsync();
        }

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Service graph -----------------------------------------------

    private async Task<IHost> BuildServices(string? redisConnectionStringOverride = null)
    {
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = AccessTokenLifetime.ToString(),
            ["Identity:Jwt:ClockSkew"] = ClockSkew.ToString(),
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKey.ExportRSAPrivateKeyPem(),
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
            ["Identity:SessionRevocationCache:ConnectionString"] = redisConnectionStringOverride ?? _redisContainer.GetConnectionString(),
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        hostBuilder.Services.AddIdentityJwtIssuance(configuration);
        hostBuilder.Services.AddIdentitySessionRevocationCache(configuration); // Api-only in production — exercised explicitly here
        hostBuilder.Services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        hostBuilder.Services.AddScoped<IIdentityTransactionExecutor, IdentityOutboxTransactionExecutor>();
        hostBuilder.Services.AddScoped<IRefreshTokenExchangeExecutor, RefreshTokenExchangeExecutor>();
        hostBuilder.Services.AddScoped<ILogoutExecutor, LogoutExecutor>();
        hostBuilder.Services.AddScoped<IAssignRoleExecutor, AssignRoleExecutor>();
        hostBuilder.Services.AddScoped<IRemoveRoleExecutor, RemoveRoleExecutor>();
        hostBuilder.Services.AddScoped<IBlockUserExecutor, BlockUserExecutor>();
        hostBuilder.Services.AddScoped<LogoutCommandHandler>();
        hostBuilder.Services.AddScoped<RefreshTokenCommandHandler>();
        hostBuilder.Services.AddScoped<AssignRoleCommandHandler>();
        hostBuilder.Services.AddScoped<RemoveRoleCommandHandler>();
        hostBuilder.Services.AddScoped<BlockUserCommandHandler>();
        hostBuilder.Services.AddScoped<UnblockUserCommandHandler>();

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that LogoutCommandHandler/RefreshTokenCommandHandler
        // actually publish Integration Events (Incremento 2 plan, Etapa 15) —
        // see LogoutCommandHandlerTests.BuildServices's doc comment for the
        // full rationale (WolverineHasNotStartedException on an unstarted host).
        await host.StartAsync();
        return host;
    }

    private static async Task<Result> ExecuteLogoutAsync(IHost root, LogoutCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<ILogoutExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<LogoutCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    private static async Task<Result<AuthTokensResult>> ExecuteRefreshAsync(IHost root, RefreshTokenCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var tenantId = await sp.GetRequiredService<ITenantBootstrapResolver<RefreshTokenCommand>>()
            .ResolveTenantAsync(command, CancellationToken.None);
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId!.Value);

        return await sp.GetRequiredService<IRefreshTokenExchangeExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RefreshTokenCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    private static async Task<Result> ExecuteAssignRoleAsync(IHost root, AssignRoleCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IAssignRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    private static async Task<Result> ExecuteRemoveRoleAsync(IHost root, RemoveRoleCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IRemoveRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    private static async Task<Result> ExecuteBlockUserAsync(IHost root, BlockUserCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IBlockUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    private static async Task<Result> ExecuteUnblockUserAsync(IHost root, UnblockUserCommand command)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IIdentityTransactionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<UnblockUserCommandHandler>().Handle(command, CancellationToken.None).AsTask(),
            CancellationToken.None);
    }

    // ---- Seeding --------------------------------------------------------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
    }

    private async Task<Guid> SeedUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, "Correct-Horse-Battery-Staple-42!"));

        var user = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task SeedUserRoleAsync(Guid tenantId, Guid userId, string roleCode, Guid assignedByUserId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        dbContext.UserRoles.Add(new UserRole(tenantId, userId, roleCode, DateTimeOffset.UtcNow, assignedByUserId));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId, Guid? sessionId = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(
            sessionId ?? Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    private static (string Presented, Guid TokenId, string TokenHash) BuildPresentedToken(
        Guid tenantId, Guid? tokenId = null, string secret = KnownSecret)
    {
        var id = tokenId ?? Guid.NewGuid();
        var presented = $"{tenantId:N}.{id:N}.{secret}";
        var hash = new RefreshTokenHasher().ComputeHash(presented);
        return (presented, id, hash);
    }

    private async Task SeedRefreshTokenAsync(
        Guid tenantId, Guid userId, Guid sessionId, Guid tokenId, string tokenHash,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, Action<RefreshToken>? mutate = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var token = RefreshToken.Issue(Guid.NewGuid(), tokenId, tenantId, sessionId, userId, tokenHash, issuedAt, expiresAt);
        mutate?.Invoke(token);
        dbContext.RefreshTokens.Add(token);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // ---- Redis verification helpers ---------------------------------------

    private static string CacheKeyFor(Guid tenantId, Guid sessionId) => $"ihostpro:{tenantId:N}:session-revoked:{sessionId:N}";

    private IDatabase ConnectToRedis()
    {
        _redisMultiplexer ??= ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString());
        return _redisMultiplexer.GetDatabase();
    }

    // ---- Tests: writes on the real revocation paths ------------------------

    [Fact]
    public async Task Logout_writes_the_session_revocation_cache_entry_after_commit()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));
        result.IsSuccess.Should().BeTrue();

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_reuse_detected_outside_the_grace_window_writes_the_session_revocation_cache_entry()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-2), now.AddDays(30),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now.AddSeconds(-30))); // well outside the 10s grace window
        using var services = await BuildServices();

        var result = await ExecuteRefreshAsync(services, new RefreshTokenCommand(
            presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")));
        result.IsFailure.Should().BeTrue(); // reuse is always rejected — cache write is a side effect, not reflected in the result

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task AssignRole_writes_the_session_revocation_cache_entry_for_the_targets_active_session_after_commit()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR"));
        result.IsSuccess.Should().BeTrue();

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveRole_writes_the_session_revocation_cache_entry_for_the_targets_active_session_after_commit()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteRemoveRoleAsync(services, new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER"));
        result.IsSuccess.Should().BeTrue();

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task BlockUser_writes_the_session_revocation_cache_entry_for_the_targets_active_session_after_commit()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, new BlockUserCommand(tenantId, actorId, targetUserId));
        result.IsSuccess.Should().BeTrue();

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task UnblockUser_never_writes_a_session_revocation_cache_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();
        // Block first (writes its own cache entry, unrelated to what's under
        // test) so the target is genuinely Blocked when Unblock runs.
        (await ExecuteBlockUserAsync(services, new BlockUserCommand(tenantId, actorId, targetUserId))).IsSuccess.Should().BeTrue();
        var redis = ConnectToRedis();
        await redis.KeyDeleteAsync(CacheKeyFor(tenantId, sessionId)); // isolate Unblock's own effect

        var result = await ExecuteUnblockUserAsync(services, new UnblockUserCommand(tenantId, actorId, targetUserId));
        result.IsSuccess.Should().BeTrue();

        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));
        value.HasValue.Should().BeFalse();
    }

    // ---- Tests: TTL ---------------------------------------------------------

    [Fact]
    public async Task Cache_entry_TTL_is_at_least_AccessTokenLifetime_plus_ClockSkew()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        var redis = ConnectToRedis();
        var ttl = await redis.KeyTimeToLiveAsync(CacheKeyFor(tenantId, sessionId));
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeCloseTo(AccessTokenLifetime + ClockSkew, TimeSpan.FromSeconds(5));
    }

    // ---- Tests: tenant isolation ---------------------------------------------

    [Fact]
    public async Task Cache_keys_are_isolated_per_tenant_even_for_the_same_session_id()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var sharedSessionId = Guid.NewGuid();
        var userA = await SeedUserAsync(tenantA);
        await SeedSessionAsync(tenantA, userA, sharedSessionId);
        using var services = await BuildServices();

        await ExecuteLogoutAsync(services, new LogoutCommand(tenantA, userA, sharedSessionId));

        var redis = ConnectToRedis();
        (await redis.StringGetAsync(CacheKeyFor(tenantA, sharedSessionId))).HasValue.Should().BeTrue();
        (await redis.StringGetAsync(CacheKeyFor(tenantB, sharedSessionId))).HasValue.Should().BeFalse();
    }

    // ---- Tests: IsRevokedAsync (Incremento 2 plan, Etapa 13 pendência) -------

    [Fact]
    public async Task IsRevokedAsync_returns_true_when_the_session_was_revoked_via_real_Redis()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();
        await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        var cache = ResolveCache(services);
        var isRevoked = await cache.IsRevokedAsync(tenantId, sessionId, CancellationToken.None);

        isRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_returns_false_for_a_session_that_was_never_revoked()
    {
        var tenantId = await SeedTenantAsync();
        using var services = await BuildServices();

        var cache = ResolveCache(services);
        var isRevoked = await cache.IsRevokedAsync(tenantId, Guid.NewGuid(), CancellationToken.None);

        isRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_is_isolated_per_tenant()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var sharedSessionId = Guid.NewGuid();
        var userA = await SeedUserAsync(tenantA);
        await SeedSessionAsync(tenantA, userA, sharedSessionId);
        using var services = await BuildServices();
        await ExecuteLogoutAsync(services, new LogoutCommand(tenantA, userA, sharedSessionId));

        var cache = ResolveCache(services);
        (await cache.IsRevokedAsync(tenantA, sharedSessionId, CancellationToken.None)).Should().BeTrue();
        (await cache.IsRevokedAsync(tenantB, sharedSessionId, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_fails_open_returning_false_when_Redis_is_unreachable()
    {
        using var services = await BuildServices(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");

        var cache = ResolveCache(services);
        var act = () => cache.IsRevokedAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_propagates_cancellation_instead_of_treating_it_as_a_cache_failure()
    {
        using var services = await BuildServices();
        var cache = ResolveCache(services);
        using var alreadyCancelled = new CancellationTokenSource();
        await alreadyCancelled.CancelAsync();

        var act = () => cache.IsRevokedAsync(Guid.NewGuid(), Guid.NewGuid(), alreadyCancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ISessionRevocationCache ResolveCache(IHost services) =>
        services.Services.CreateScope().ServiceProvider.GetRequiredService<ISessionRevocationCache>();

    // ---- Tests: idempotency ---------------------------------------------------

    [Fact]
    public async Task Repeated_logout_does_not_error_and_the_cache_entry_remains_present()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        var first = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));
        var second = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        var redis = ConnectToRedis();
        (await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId))).HasValue.Should().BeTrue();
    }

    // ---- Tests: Redis unavailable ---------------------------------------------

    [Fact]
    public async Task Logout_still_succeeds_when_Redis_is_unreachable()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        // An address nothing listens on, with a short connect timeout via the
        // connection string, so the test does not hang.
        using var services = await BuildServices(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");

        var act = async () => await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AssignRole_still_succeeds_when_Redis_is_unreachable()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");

        var act = async () => await ExecuteAssignRoleAsync(services, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR"));

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveRole_still_succeeds_when_Redis_is_unreachable()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");

        var act = async () => await ExecuteRemoveRoleAsync(services, new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER"));

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task BlockUser_still_succeeds_when_Redis_is_unreachable()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");

        var act = async () => await ExecuteBlockUserAsync(services, new BlockUserCommand(tenantId, actorId, targetUserId));

        var result = await act.Should().NotThrowAsync();
        result.Subject.IsSuccess.Should().BeTrue();
    }

    // ---- Tests: no sensitive data -----------------------------------------------

    [Fact]
    public async Task The_cached_value_carries_no_sensitive_data()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        var redis = ConnectToRedis();
        var value = await redis.StringGetAsync(CacheKeyFor(tenantId, sessionId));

        // A fixed, non-sensitive presence marker — never a token, hash, or
        // any user-identifying data. Length-bounded assertion, not just a
        // substring check, so a future accidental change that appends real
        // data would still fail this test.
        value.ToString().Should().Be("1");
    }

    // ---- Helpers ----------------------------------------------------------

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
