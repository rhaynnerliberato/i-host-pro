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
using IHostPro.Contexts.Identity.Application.Users;
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
/// End-to-end test of the AssignRole use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 6) — mirrors
/// <see cref="CreateUserCommandHandlerTests"/>'s structure exactly: manually
/// chained the way <c>AssignRoleTenantAwareBehavior</c> would, tenant/actor
/// already resolved from an authenticated Administrator's claims. Focuses on
/// PostgreSQL-observable state (UserRole/audit rows); event CONTENT
/// assertions (payload shape, CausationId chaining, ActorId, no-event-on-
/// rejection) live in <see cref="IdentityIntegrationEventsTests"/> alongside
/// the other Identity events, not here.
/// </summary>
public class AssignRoleCommandHandlerTests : IClassFixture<AssignRoleCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public AssignRoleCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>Started once per test class — see <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for the full rationale.</summary>
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
        hostBuilder.Services.AddScoped<IAssignRoleExecutor, AssignRoleExecutor>();
        hostBuilder.Services.AddScoped<AssignRoleCommandHandler>();

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that AssignRoleCommandHandler publishes Integration
        // Events: IDbContextOutbox<T>.PublishAsync throws
        // WolverineHasNotStartedException against a host that was only
        // Build() and never started.
        await host.StartAsync();
        return host;
    }

    private static async Task<Result> ExecuteAssignRoleAsync(
        IHost root, AssignRoleCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new AssignRoleCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IAssignRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    // ---- Seeding ----------------------------------------------------------

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
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
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

    /// <summary>
    /// Seeds one active session and one still-active refresh token under it —
    /// used by the Checkpoint 9 follow-up regression proving the
    /// <see cref="IUserSessionRevoker"/> cascade this handler shares with
    /// RemoveRole/Block/ChangeOwnPassword/AdminResetPassword genuinely
    /// persists to PostgreSQL (not just events/Redis) since the
    /// <c>SessionReader</c> <c>AsNoTracking()</c> fix.
    /// </summary>
    private async Task<(Guid SessionId, Guid RefreshTokenId)> SeedActiveSessionWithRefreshTokenAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        var session = Session.Open(Guid.NewGuid(), tenantId, userId, now, device: null, browser: null, ipAddress: null);
        dbContext.Sessions.Add(session);

        var refreshToken = RefreshToken.Issue(
            Guid.NewGuid(), Guid.NewGuid(), tenantId, session.Id, userId, "irrelevant-hash", now, now.AddDays(30));
        dbContext.RefreshTokens.Add(refreshToken);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (session.Id, refreshToken.Id);
    }

    private static AssignRoleCommand ValidCommand(Guid tenantId, Guid actorId, Guid targetUserId, string roleCode = "OPERATOR") =>
        new(tenantId, actorId, targetUserId, roleCode);

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_assignment_persists_the_role_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var userRole = await dbContext.UserRoles.SingleAsync(ur => ur.UserId == targetUserId);
        userRole.RoleCode.Should().Be("OPERATOR");
        userRole.AssignedByUserId.Should().Be(actorId);

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == targetUserId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.UserRoleAssigned);
        // Fase 12, Checkpoint 4 — the acting administrator, never the target, ends up in ActorId.
        auditEntries[0].ActorId.Should().Be(actorId);
        auditEntries[0].UserId.Should().Be(targetUserId);
    }

    /// <summary>
    /// Checkpoint 9 follow-up regression (mandatory per review): proves the
    /// session-revocation cascade this handler shares with RemoveRole/Block/
    /// ChangeOwnPassword/AdminResetPassword genuinely persists to PostgreSQL —
    /// not just events, audit or Redis. This test would have FAILED against
    /// the pre-fix <c>SessionReader</c> (which loaded sessions with
    /// <c>AsNoTracking()</c> for this cascade, so <c>Session.Revoke()</c>/
    /// <c>RefreshToken.Revoke()</c> never made it into <c>SaveChangesAsync</c>).
    /// </summary>
    [Fact]
    public async Task A_valid_assignment_persists_the_targets_session_and_refresh_token_revocation()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var (sessionId, refreshTokenId) = await SeedActiveSessionWithRefreshTokenAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be(SessionRevokedReasonCodes.RolesChanged);

        var refreshToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.Id == refreshTokenId);
        refreshToken.IsRevoked.Should().BeTrue();
        refreshToken.RevokedAt.Should().NotBeNull();
        refreshToken.RevocationReason.Should().Be(RefreshTokenRevocationReason.RolesChanged);
    }

    // ---- Tests: rejections -------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.UserRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_user_of_a_different_tenant_is_indistinguishable_from_nonexistent()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantA);
        var userInTenantB = await SeedUserAsync(tenantB);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantA, actorId, userInTenantB));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task A_nonexistent_role_code_fails_with_RoleNotFound_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, targetUserId, "NOT_A_REAL_ROLE"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleNotFound);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.UserRoles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_role_already_assigned_fails_with_RoleAlreadyAssigned_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var services = await BuildServices();

        var result = await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleAlreadyAssigned);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId)).Should().Be(1); // still just the one seeded
        (await dbContext.SecurityAuditLog.CountAsync()).Should().Be(0);
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_role_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteAssignRoleAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.UserRoles.CountAsync()).Should().Be(0);
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after UserRole was staged.");
    }

    // ---- Helpers ------------------------------------------------------------

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
