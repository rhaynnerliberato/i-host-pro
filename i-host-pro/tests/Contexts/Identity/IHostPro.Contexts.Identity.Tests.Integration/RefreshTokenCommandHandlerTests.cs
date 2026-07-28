using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of the Refresh Token exchange use case against a real
/// PostgreSQL instance (Incremento 2 plan, Etapa 10):
/// <see cref="RefreshTokenTenantBootstrapResolver"/> +
/// <see cref="RefreshTokenCommandHandler"/>, manually chained exactly the way
/// <c>TenantBootstrapBehavior</c> would — same structure as
/// <see cref="LoginCommandHandlerTests"/>.
/// </summary>
public class RefreshTokenCommandHandlerTests : IClassFixture<RefreshTokenCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownSecret = "known-secret-segment-for-tests";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public RefreshTokenCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

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

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();

            var adminConnectionString = _container.GetConnectionString();

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

        public async Task DisposeAsync() => await _container.DisposeAsync();

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

    private async Task<IHost> BuildServices(
        DateTimeOffset? now = null, TimeSpan? graceWindow = null, Action<IServiceCollection>? overrides = null)
    {
        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = (graceWindow ?? TimeSpan.FromSeconds(10)).ToString(),
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        hostBuilder.Services.AddIdentityJwtIssuance(configuration);
        hostBuilder.Services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        hostBuilder.Services.AddScoped<IIdentityTransactionExecutor, IdentityOutboxTransactionExecutor>();
        hostBuilder.Services.AddScoped<IRefreshTokenExchangeExecutor, RefreshTokenExchangeExecutor>();
        hostBuilder.Services.AddScoped<ILogoutExecutor, LogoutExecutor>();
        hostBuilder.Services.AddScoped<RefreshTokenCommandHandler>();

        if (now is not null)
        {
            var fixedTimeProvider = new FixedTimeProvider(now.Value);
            hostBuilder.Services.AddSingleton<TimeProvider>(fixedTimeProvider);
        }

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that RefreshTokenCommandHandler actually publishes
        // Integration Events (Incremento 2 plan, Etapa 15) — see
        // LogoutCommandHandlerTests.BuildServices's doc comment for the full
        // rationale (WolverineHasNotStartedException on an unstarted host).
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Manually replicates ValidationBehavior -> TenantBootstrapBehavior -> handler,
    /// exactly as the (not-yet-wired) Mediator dispatch would.
    /// </summary>
    private static async Task<Result<AuthTokensResult>> ExecuteRefreshAsync(
        IHost root, RefreshTokenCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new RefreshTokenCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<AuthTokensResult>(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var tenantId = await sp.GetRequiredService<ITenantBootstrapResolver<RefreshTokenCommand>>()
            .ResolveTenantAsync(command, cancellationToken);

        if (tenantId is null)
            return Result.Failure<AuthTokensResult>(new Error("Tenant.NotFound", "The tenant could not be resolved."));

        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId.Value);

        // IRefreshTokenExchangeExecutor wraps IIdentityTransactionExecutor with a
        // small, Refresh-specific retry for DbUpdateConcurrencyException —
        // see its own doc comment (Incremento 2 plan, Etapa 10 correction:
        // the shared executor no longer retries anything itself).
        // Resolved by interface here deliberately — this is exactly how the
        // future refresh endpoint must depend on it too (Etapa 10 -> 11
        // pendência: never the concrete Infrastructure class).
        return await sp.GetRequiredService<IRefreshTokenExchangeExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RefreshTokenCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    // ---- Seeding --------------------------------------------------------

    private async Task<Guid> SeedTenantAsync(bool active = true)
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        if (!active)
            tenant.Suspend();
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
    }

    private async Task<Guid> SeedUserAsync(Guid tenantId, bool blocked = false)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, "Correct-Horse-Battery-Staple-42!"));

        var user = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, DateTimeOffset.UtcNow);
        if (blocked)
            user.Block(DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task AssignRoleAsync(Guid tenantId, Guid userId, string roleCode)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        dbContext.UserRoles.Add(new UserRole(tenantId, userId, roleCode, DateTimeOffset.UtcNow, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId, bool active = true)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        if (!active)
            session.Revoke("test_setup_revoked", DateTimeOffset.UtcNow);
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    /// <summary>
    /// Builds the canonical wire-format string (<c>{tenantId:N}.{tokenId:N}.{secret}</c>)
    /// and its SHA-256 hash without going through <see cref="IRefreshTokenGenerator"/>,
    /// so the test controls the exact TokenId/secret and can then seed a
    /// matching (or deliberately mismatching) <see cref="RefreshToken"/> row.
    /// </summary>
    private static (string Presented, Guid TokenId, string TokenHash) BuildPresentedToken(
        Guid tenantId, Guid? tokenId = null, string secret = KnownSecret)
    {
        var id = tokenId ?? Guid.NewGuid();
        var presented = $"{tenantId:N}.{id:N}.{secret}";
        var hash = new RefreshTokenHasher().ComputeHash(presented);
        return (presented, id, hash);
    }

    private async Task<RefreshToken> SeedRefreshTokenAsync(
        Guid tenantId, Guid userId, Guid sessionId, Guid tokenId, string tokenHash,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, Guid? previousTokenId = null,
        Action<RefreshToken>? mutate = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var token = RefreshToken.Issue(
            Guid.NewGuid(), tokenId, tenantId, sessionId, userId, tokenHash, issuedAt, expiresAt, previousTokenId);
        mutate?.Invoke(token);
        dbContext.RefreshTokens.Add(token);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return token;
    }

    private async Task<string> GetTenantSlugAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        return tenant.Slug.Value;
    }

    private static RefreshTokenCommand RefreshAs(string presentedToken) =>
        new(presentedToken, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task Refresh_with_a_valid_token_succeeds_and_rotates_the_chain()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.Value.RefreshToken.Should().NotBe(presented);
        result.Value.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task Successful_refresh_rotates_the_chain_correctly_and_touches_the_session()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var oldToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        var newToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.PreviousTokenId == tokenId);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);

        oldToken.IsRevoked.Should().BeTrue();
        oldToken.RevocationReason.Should().Be(RefreshTokenRevocationReason.Rotated);
        oldToken.ReplacedByTokenId.Should().Be(newToken.TokenId);
        newToken.PreviousTokenId.Should().Be(oldToken.TokenId);
        newToken.UserId.Should().Be(userId);
        newToken.SessionId.Should().Be(sessionId);
        // PostgreSQL timestamptz has microsecond precision; DateTimeOffset
        // has 100ns ticks — an exact Be() can fail on the sub-microsecond
        // remainder after a DB round-trip even though nothing is wrong.
        session.LastActivityAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));

        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.EventType == SecurityAuditEventType.RefreshSucceeded);
        audit.UserId.Should().Be(userId);
        audit.SessionId.Should().Be(sessionId);
        audit.RefreshTokenId.Should().Be(newToken.TokenId);
    }

    [Fact]
    public async Task Refresh_result_includes_every_assigned_role()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        await AssignRoleAsync(tenantId, userId, "ADMIN");
        await AssignRoleAsync(tenantId, userId, "OPERATOR");
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        var jwt = new JsonWebToken(result.Value.AccessToken);
        jwt.GetPayloadValue<string[]>("role").Should().BeEquivalentTo(["ADMIN", "OPERATOR"]);
    }

    [Fact]
    public async Task Refresh_succeeds_with_no_assigned_roles()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsSuccess.Should().BeTrue();
        var jwt = new JsonWebToken(result.Value.AccessToken);
        jwt.GetPayloadValue<string[]>("role").Should().BeEmpty();
    }

    // ---- Tests: rejection paths -------------------------------------------

    [Fact]
    public async Task Refresh_with_a_malformed_token_returns_the_generic_failure()
    {
        using var services = await BuildServices();

        var result = await ExecuteRefreshAsync(services, RefreshAs("not-a-valid-token-format"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_for_an_unresolved_or_inactive_tenant_returns_the_same_generic_failure()
    {
        var inactiveTenantId = await SeedTenantAsync(active: false);
        using var services = await BuildServices();
        var (presentedForInactive, _, _) = BuildPresentedToken(inactiveTenantId);
        var (presentedForUnresolved, _, _) = BuildPresentedToken(Guid.NewGuid());

        var forInactive = await ExecuteRefreshAsync(services, RefreshAs(presentedForInactive));
        var forUnresolved = await ExecuteRefreshAsync(services, RefreshAs(presentedForUnresolved));

        forInactive.IsFailure.Should().BeTrue();
        forInactive.Error.Should().Be(forUnresolved.Error);
    }

    [Fact]
    public async Task Refresh_with_an_incorrect_secret_returns_the_generic_failure_and_records_a_hash_mismatch()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        var (wrongPresented, _, _) = BuildPresentedToken(tenantId, tokenId, secret: "a-completely-different-secret");
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(wrongPresented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.RefreshTokenHashMismatch);

        var untouchedToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        untouchedToken.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_with_an_unknown_TokenId_returns_the_same_generic_failure_as_an_incorrect_secret()
    {
        var tenantId = await SeedTenantAsync();
        using var services = await BuildServices();
        var (unknownPresented, tokenId, _) = BuildPresentedToken(tenantId);

        var result = await ExecuteRefreshAsync(services, RefreshAs(unknownPresented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.RefreshTokenHashMismatch);
    }

    [Fact]
    public async Task Refresh_for_a_blocked_user_returns_the_generic_failure()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, blocked: true);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.UserBlocked);
    }

    [Fact]
    public async Task Refresh_for_an_inactive_session_is_rejected_without_overwriting_the_reason_as_reuse()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId, active: false);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.SessionNotActive);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.RevocationReason.Should().Be("test_setup_revoked"); // untouched, not overwritten
    }

    [Fact]
    public async Task Refresh_with_an_expired_token_preserves_the_Expired_reason_and_is_not_treated_as_reuse()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var issuedAt = DateTimeOffset.UtcNow.AddDays(-31);
        var expiresAt = issuedAt.AddDays(30); // already in the past
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, issuedAt, expiresAt);
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.RefreshTokenExpired);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active); // untouched
    }

    [Theory]
    [InlineData(RefreshTokenRevocationReason.LogoutRequested, SecurityAuditReasonCode.LogoutRequested)]
    [InlineData(RefreshTokenRevocationReason.AdminRevoked, SecurityAuditReasonCode.AdminRevoked)]
    public async Task Refresh_of_a_token_revoked_for_logout_or_administration_preserves_the_original_reason(
        RefreshTokenRevocationReason revocationReason, SecurityAuditReasonCode expectedReasonCode)
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-5), now.AddDays(30),
            mutate: t => t.Revoke(revocationReason, now.AddMinutes(-1)));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(expectedReasonCode);

        var token = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        token.RevocationReason.Should().Be(revocationReason); // preserved, not overwritten

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active); // never revoked as a side effect
    }

    // ---- Tests: rotated-again classification (grace window vs reuse) ------

    [Fact]
    public async Task Presenting_an_already_rotated_token_within_the_grace_window_is_rejected_without_revoking_the_session()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var rotatedAt = now.AddSeconds(-3);
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30),
            mutate: t => t.MarkRotated(Guid.NewGuid(), rotatedAt));
        using var services = await BuildServices(now, graceWindow: TimeSpan.FromSeconds(10));

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.ConcurrentRotationGraceWindow);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task Presenting_an_already_rotated_token_outside_the_grace_window_is_detected_as_reuse_and_revokes_the_session()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var rotatedAt = now.AddSeconds(-30);
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-2), now.AddDays(30),
            mutate: t => t.MarkRotated(Guid.NewGuid(), rotatedAt));
        using var services = await BuildServices(now, graceWindow: TimeSpan.FromSeconds(10));

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.RefreshTokenId == tokenId);
        audit.EventType.Should().Be(SecurityAuditEventType.RefreshTokenReuseDetected);
        audit.ReasonCode.Should().BeNull();

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevocationReason.Should().Be("refresh_token_reuse_detected");
    }

    // ---- Tests: concurrency ------------------------------------------------

    [Fact]
    public async Task Two_concurrent_refresh_attempts_with_the_same_token_result_in_exactly_one_success()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now, graceWindow: TimeSpan.FromMinutes(1));

        var first = ExecuteRefreshAsync(services, RefreshAs(presented));
        var second = ExecuteRefreshAsync(services, RefreshAs(presented));
        var results = await Task.WhenAll(first, second);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.IsFailure).Should().Be(1);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        // Exactly one successor was issued — the loser never got far enough
        // to stage a second one (it observed the token as already Rotated).
        (await dbContext.RefreshTokens.CountAsync(rt => rt.PreviousTokenId == tokenId)).Should().Be(1);

        var loserAudit = await dbContext.SecurityAuditLog
            .SingleAsync(e => e.RefreshTokenId == tokenId && e.EventType == SecurityAuditEventType.RefreshRejected);
        loserAudit.ReasonCode.Should().Be(SecurityAuditReasonCode.ConcurrentRotationGraceWindow);
    }

    // ---- Tests: rollback ---------------------------------------------------

    [Fact]
    public async Task A_failure_after_rotation_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now, overrides: sc =>
            sc.AddScoped<IJwtTokenGenerator, ThrowingJwtTokenGenerator>());

        var act = async () => await ExecuteRefreshAsync(services, RefreshAs(presented));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var untouchedToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        untouchedToken.IsRevoked.Should().BeFalse();
        (await dbContext.RefreshTokens.CountAsync(rt => rt.PreviousTokenId == tokenId)).Should().Be(0);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.RefreshTokenId == tokenId)).Should().Be(0);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.LastActivityAt.Should().BeBefore(now.AddSeconds(1)); // Touch never persisted
    }

    private sealed class ThrowingJwtTokenGenerator : IJwtTokenGenerator
    {
        public JwtAccessTokenResult GenerateAccessToken(JwtAccessTokenRequest request) =>
            throw new InvalidOperationException("Simulated failure after rotation was staged.");
    }

    // ---- Tests: tenant isolation --------------------------------------------

    [Fact]
    public async Task A_token_belonging_to_one_tenant_is_invisible_when_the_wire_format_is_tampered_to_claim_another()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantA);
        var sessionId = await SeedSessionAsync(tenantA, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantA);
        await SeedRefreshTokenAsync(tenantA, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));

        // Same tokenId/secret, but the tenant segment is swapped to a
        // DIFFERENT, equally active tenant — RefreshTokenTenantBootstrapResolver
        // happily resolves tenant B (it is genuinely active), but RLS then
        // makes tenant A's row invisible to a transaction scoped to tenant B.
        var tampered = $"{tenantB:N}.{tokenId:N}.{KnownSecret}";
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(tampered));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantA);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantA);
        var untouchedToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        untouchedToken.IsRevoked.Should().BeFalse();
    }

    // ---- Tests: no sensitive data leakage -----------------------------------

    [Fact]
    public async Task No_rejection_reveals_the_presented_token_anywhere_in_the_result()
    {
        var tenantId = await SeedTenantAsync();
        using var services = await BuildServices();
        var (presented, _, _) = BuildPresentedToken(tenantId, secret: "this-must-never-leak-into-any-error");

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));

        result.Error.Code.Should().NotContain(presented);
        result.Error.Message.Should().NotContain(presented);
        result.Error.Code.Should().NotContain("this-must-never-leak-into-any-error");
    }

    [Fact]
    public async Task Audit_log_never_stores_the_presented_token_new_token_or_hash()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(now);

        var result = await ExecuteRefreshAsync(services, RefreshAs(presented));
        result.IsSuccess.Should().BeTrue();

        var columns = typeof(SecurityAuditEntry).GetProperties().Select(p => p.Name);
        columns.Should().NotContain(["Password", "AccessToken", "RefreshToken", "TokenHash"]);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var newToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.PreviousTokenId == tokenId);

        newToken.TokenHash.Should().NotBe(result.Value.RefreshToken);
        newToken.TokenHash.Should().MatchRegex("^[0-9a-f]{64}$");
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
