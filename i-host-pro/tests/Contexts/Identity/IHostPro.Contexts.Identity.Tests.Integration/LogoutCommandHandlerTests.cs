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
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of the Logout use case against a real PostgreSQL instance
/// (Incremento 2 plan, Etapa 11): <see cref="LogoutCommandHandler"/>, manually
/// chained the way <c>TenantTransactionBehavior</c> would for a
/// non-bootstrap command (tenant already resolved from an authenticated
/// claim — no <c>ITenantBootstrapResolver</c> involved, unlike Login/Refresh).
/// </summary>
public class LogoutCommandHandlerTests : IClassFixture<LogoutCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownSecret = "known-secret-segment-for-tests";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public LogoutCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
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

    private async Task<IHost> BuildServices(Action<IServiceCollection>? overrides = null)
    {
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKey.ExportRSAPrivateKeyPem(),
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
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
        hostBuilder.Services.AddScoped<LogoutCommandHandler>();
        hostBuilder.Services.AddScoped<RefreshTokenCommandHandler>(); // needed for the Logout/Refresh concurrency test below

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that LogoutCommandHandler/RefreshTokenCommandHandler
        // actually publish Integration Events (Incremento 2 plan, Etapa 15):
        // IDbContextOutbox<T>.PublishAsync throws WolverineHasNotStartedException
        // against a host that was only Build() and never started — confirmed
        // empirically. No RabbitMQ transport/routing is configured in this
        // file (none of these tests assert on event delivery, only on the
        // Logout/Refresh business outcome), which is safe: an unrouted
        // message does not fail the publish/commit itself, only its later,
        // asynchronous relay.
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Manually replicates ValidationBehavior -> TenantTransactionBehavior -> handler
    /// for a NON-bootstrap command: the tenant is already resolved (simulating an
    /// authenticated JWT claim), never derived from anything client-supplied.
    /// </summary>
    private static async Task<Result> ExecuteLogoutAsync(
        IHost root, LogoutCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new LogoutCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        // ILogoutExecutor wraps IIdentityTransactionExecutor with a small,
        // Logout-specific retry for DbUpdateConcurrencyException (Incremento
        // 2 plan, Etapa 11 correction) — resolved by interface here
        // deliberately, exactly how the future logout endpoint must depend
        // on it too.
        return await sp.GetRequiredService<ILogoutExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<LogoutCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

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

        return await sp.GetRequiredService<IRefreshTokenExchangeExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RefreshTokenCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
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

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
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

    private async Task<Guid> SeedRefreshTokenAsync(
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

        return token.Id;
    }

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task Logout_revokes_the_session_and_the_active_refresh_token_and_records_audit()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevocationReason.Should().Be("LogoutRequested");

        var token = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        token.IsRevoked.Should().BeTrue();
        token.RevocationReason.Should().Be(RefreshTokenRevocationReason.LogoutRequested);

        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.SessionId == sessionId);
        audit.EventType.Should().Be(SecurityAuditEventType.LogoutSucceeded);
        audit.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task Logout_revokes_every_still_active_refresh_token_of_the_session()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenIdA, hashA) = BuildPresentedToken(tenantId);
        var (_, tokenIdB, hashB) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenIdA, hashA, now.AddMinutes(-2), now.AddDays(30));
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenIdB, hashB, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tokens = await dbContext.RefreshTokens.Where(rt => rt.SessionId == sessionId).ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Should().OnlyContain(t => t.RevocationReason == RefreshTokenRevocationReason.LogoutRequested);
    }

    // ---- Tests: idempotency -------------------------------------------------

    [Fact]
    public async Task Repeating_logout_is_idempotent_and_writes_no_further_audit_entries()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices();

        var first = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));
        var second = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.SecurityAuditLog.CountAsync(e => e.SessionId == sessionId)).Should().Be(1);
    }

    [Fact]
    public async Task Logout_for_a_nonexistent_session_succeeds_without_writing_any_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, Guid.NewGuid()));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task Logout_for_a_session_belonging_to_a_different_user_succeeds_without_touching_it()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedUserAsync(tenantId);
        var otherUserId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, ownerId);
        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, otherUserId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task Logout_for_a_session_belonging_to_a_different_tenant_succeeds_without_touching_it()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantA);
        var sessionId = await SeedSessionAsync(tenantA, userId);
        using var services = await BuildServices();

        // Same session/user id, but the tenant claim points at tenant B — RLS
        // makes tenant A's session row invisible to a transaction scoped to B.
        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantB, userId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantA);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantA);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
    }

    // ---- Tests: historical tokens preserve their reason --------------------

    [Fact]
    public async Task Logout_never_overwrites_the_reason_of_already_rotated_expired_or_revoked_tokens()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;

        var (_, rotatedTokenId, rotatedHash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, rotatedTokenId, rotatedHash, now.AddMinutes(-10), now.AddDays(30),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now.AddMinutes(-5)));

        var (_, expiredTokenId, expiredHash) = BuildPresentedToken(tenantId);
        var expiredIssuedAt = now.AddDays(-31);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, expiredTokenId, expiredHash, expiredIssuedAt, expiredIssuedAt.AddDays(30));

        var (_, adminRevokedTokenId, adminRevokedHash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, adminRevokedTokenId, adminRevokedHash, now.AddMinutes(-3), now.AddDays(30),
            mutate: t => t.Revoke(RefreshTokenRevocationReason.AdminRevoked, now.AddMinutes(-2)));

        var (_, activeTokenId, activeHash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, activeTokenId, activeHash, now.AddMinutes(-1), now.AddDays(30));

        using var services = await BuildServices();

        var result = await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == rotatedTokenId)).RevocationReason
            .Should().Be(RefreshTokenRevocationReason.Rotated);
        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == expiredTokenId)).IsRevoked.Should().BeFalse();
        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == adminRevokedTokenId)).RevocationReason
            .Should().Be(RefreshTokenRevocationReason.AdminRevoked);
        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == activeTokenId)).RevocationReason
            .Should().Be(RefreshTokenRevocationReason.LogoutRequested);
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_session_and_token_were_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
        var token = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        token.IsRevoked.Should().BeFalse();
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after Session/RefreshToken were staged.");
    }

    // ---- Tests: concurrency with refresh -------------------------------------

    [Fact]
    public async Task Concurrent_logout_and_refresh_converge_in_a_single_call_with_no_exception()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices();

        // Fire both concurrently against the exact same session/token.
        // ILogoutExecutor/IRefreshTokenExchangeExecutor each retry a bounded
        // number of times on a genuine xmin conflict, so neither call is
        // expected to throw — no try/catch, no tolerance (Incremento 2 plan,
        // Etapa 11 correction).
        var logoutTask = ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));
        var refreshTask = ExecuteRefreshAsync(services, new RefreshTokenCommand(
            presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")));
        var logoutResult = await logoutTask;
        await refreshTask; // may legitimately fail (SessionNotActive) depending on ordering — not asserted

        // The single logout call above — no additional sequential call — must
        // already have converged the system to a fully revoked state.
        logoutResult.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);

        var stillActiveTokens = await dbContext.RefreshTokens
            .Where(rt => rt.SessionId == sessionId && rt.RevokedAt == null)
            .ToListAsync();
        stillActiveTokens.Should().BeEmpty();
    }

    // ---- Tests: no sensitive data leakage -----------------------------------

    [Fact]
    public async Task Audit_log_schema_cannot_hold_a_password_token_or_hash()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        await ExecuteLogoutAsync(services, new LogoutCommand(tenantId, userId, sessionId));

        var columns = typeof(SecurityAuditEntry).GetProperties().Select(p => p.Name);
        columns.Should().NotContain(["Password", "AccessToken", "RefreshToken", "TokenHash"]);
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
