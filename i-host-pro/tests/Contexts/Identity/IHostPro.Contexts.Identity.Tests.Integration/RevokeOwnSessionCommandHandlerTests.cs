using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
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
/// End-to-end test of the RevokeOwnSession use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 4) — mirrors <see cref="LogoutCommandHandlerTests"/>'s
/// structure exactly (manually chained the way <c>RevokeOwnSessionTenantAwareBehavior</c>
/// would, tenant already resolved from an authenticated claim). Unlike
/// Logout, a "session not found/foreign/inactive" outcome here is a genuine
/// <see cref="Result.Failure"/> (<see cref="IdentityErrorCodes.SessionNotOwnedByUser"/>),
/// never an idempotent success — see <see cref="RevokeOwnSessionCommandHandler"/>'s
/// own doc comment for why. Outbox/RabbitMQ/broker-outage coverage for the
/// <c>SessionRevoked</c> envelope this command publishes lives in
/// <see cref="IdentityIntegrationEventsTests"/> alongside the other five
/// events, not here.
/// </summary>
public class RevokeOwnSessionCommandHandlerTests : IClassFixture<RevokeOwnSessionCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownSecret = "known-secret-segment-for-tests";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public RevokeOwnSessionCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale. Also provisions Identity's outbox, mirroring
    /// <see cref="LogoutCommandHandlerTests.Fixture"/>, since
    /// <see cref="IRevokeOwnSessionExecutor"/> depends on
    /// <see cref="IIdentityTransactionExecutor"/>, which needs
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
        hostBuilder.Services.AddScoped<IRevokeOwnSessionExecutor, RevokeOwnSessionExecutor>();
        hostBuilder.Services.AddScoped<RevokeOwnSessionCommandHandler>();

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that RevokeOwnSessionCommandHandler publishes an
        // Integration Event: IDbContextOutbox<T>.PublishAsync throws
        // WolverineHasNotStartedException against a host that was only
        // Build() and never started — confirmed empirically for the
        // equivalent Logout/Refresh tests. No RabbitMQ transport/routing is
        // configured in this file (none of these tests assert on event
        // delivery, only on the business outcome), which is safe: an
        // unrouted message does not fail the publish/commit itself.
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Manually replicates ValidationBehavior -> RevokeOwnSessionTenantAwareBehavior -> handler:
    /// the tenant is already resolved (simulating an authenticated JWT
    /// claim), never derived from anything client-supplied.
    /// </summary>
    private static async Task<Result> ExecuteRevokeOwnSessionAsync(
        IHost root, RevokeOwnSessionCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new RevokeOwnSessionCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IRevokeOwnSessionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RevokeOwnSessionCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId, bool revoked = false)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        if (revoked)
            session.Revoke("PreviouslyRevokedForTest", DateTimeOffset.UtcNow);
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

        return token.Id;
    }

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_active_session_is_revoked_along_with_its_active_refresh_token_and_audited()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        using var services = await BuildServices();

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevocationReason.Should().Be(SessionRevokedReasonCodes.UserRequestedRevocation);

        var token = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        token.IsRevoked.Should().BeTrue();
        token.RevocationReason.Should().Be(RefreshTokenRevocationReason.UserRequestedRevocation);

        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.SessionId == sessionId);
        audit.EventType.Should().Be(SecurityAuditEventType.SessionRevoked);
        audit.UserId.Should().Be(userId);
        audit.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Every_still_active_refresh_token_of_the_session_is_revoked()
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

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tokens = await dbContext.RefreshTokens.Where(rt => rt.SessionId == sessionId).ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Should().OnlyContain(t => t.RevocationReason == RefreshTokenRevocationReason.UserRequestedRevocation);
    }

    // ---- Tests: not owned by the caller -> failure, no side effect ------------

    [Fact]
    public async Task A_nonexistent_session_fails_with_SessionNotOwnedByUser_and_writes_no_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.SessionNotOwnedByUser);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task A_session_belonging_to_a_different_user_fails_and_is_left_untouched()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedUserAsync(tenantId);
        var otherUserId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, ownerId);
        using var services = await BuildServices();

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, otherUserId, sessionId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.SessionNotOwnedByUser);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task A_session_belonging_to_a_different_tenant_fails_and_is_left_untouched()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantA);
        var sessionId = await SeedSessionAsync(tenantA, userId);
        using var services = await BuildServices();

        // Same session/user id, but the tenant claim points at tenant B — RLS
        // makes tenant A's session row invisible to a transaction scoped to B.
        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantB, userId, sessionId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.SessionNotOwnedByUser);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantA);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantA);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task An_already_revoked_session_fails_and_its_revocation_reason_is_not_overwritten()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId, revoked: true);
        using var services = await BuildServices();

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.SessionNotOwnedByUser);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.RevocationReason.Should().Be("PreviouslyRevokedForTest");
    }

    // ---- Tests: historical tokens preserve their reason --------------------

    [Fact]
    public async Task Revocation_never_overwrites_the_reason_of_already_rotated_expired_or_revoked_tokens()
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

        var (_, activeTokenId, activeHash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, activeTokenId, activeHash, now.AddMinutes(-1), now.AddDays(30));

        using var services = await BuildServices();

        var result = await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == rotatedTokenId)).RevocationReason
            .Should().Be(RefreshTokenRevocationReason.Rotated);
        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == expiredTokenId)).IsRevoked.Should().BeFalse();
        (await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == activeTokenId)).RevocationReason
            .Should().Be(RefreshTokenRevocationReason.UserRequestedRevocation);
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

        var act = async () => await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Active);
        var token = await dbContext.RefreshTokens.SingleAsync(rt => rt.TokenId == tokenId);
        token.IsRevoked.Should().BeFalse();
    }

    /// <summary>
    /// <see cref="RevokeOwnSessionCommandHandler"/> stages the revocation
    /// signal BEFORE writing the audit entry — this test's
    /// <see cref="ThrowingSecurityAuditWriter"/> override throws after that
    /// staging point, proving the signal being set does not, by itself,
    /// cause <see cref="RevokeOwnSessionExecutor"/> to reach the post-commit
    /// cache-write loop: that loop is structurally placed after
    /// <see cref="IIdentityTransactionExecutor.ExecuteAsync{TResponse}"/>
    /// returns successfully, which never happens on this path (Incremento 3,
    /// Checkpoint 4, Section 8: "falha antes do commit não produz sinal
    /// Redis efetivo").
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_signal_was_staged_never_writes_to_the_revocation_cache()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (_, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(tenantId, userId, sessionId, tokenId, hash, now.AddMinutes(-1), now.AddDays(30));
        var recordingCache = new RecordingSessionRevocationCache();
        using var services = await BuildServices(overrides: sc =>
        {
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>();
            sc.AddScoped<ISessionRevocationCache>(_ => recordingCache);
        });

        var act = async () => await ExecuteRevokeOwnSessionAsync(services, new RevokeOwnSessionCommand(tenantId, userId, sessionId));

        await act.Should().ThrowAsync<InvalidOperationException>();
        recordingCache.MarkRevokedCallCount.Should().Be(0);
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after Session/RefreshToken were staged.");
    }

    private sealed class RecordingSessionRevocationCache : ISessionRevocationCache
    {
        public int MarkRevokedCallCount { get; private set; }

        public Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
        {
            MarkRevokedCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
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
