using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
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
/// End-to-end test of the UnblockUser use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 7) — mirrors
/// <see cref="BlockUserCommandHandlerTests"/>'s Fixture/seeding shape, but
/// deliberately narrower: no <see cref="ILastAdministratorGuard"/>
/// consultation, no session/refresh-token cascade, no real-concurrency test
/// (see <c>UnblockUserCommandHandler</c>'s own doc comment for why no
/// genuine <c>DbUpdateConcurrencyException</c> risk exists here — the same
/// reasoning means there is no meaningful concurrent-Unblock race to prove).
/// </summary>
public class UnblockUserCommandHandlerTests : IClassFixture<UnblockUserCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public UnblockUserCommandHandlerTests(Fixture fixture)
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
        hostBuilder.Services.AddScoped<UnblockUserCommandHandler>();

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Result> ExecuteUnblockUserAsync(
        IHost root, UnblockUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new UnblockUserCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IIdentityTransactionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<UnblockUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    private async Task<Guid> SeedUserAsync(Guid tenantId, bool blocked = false)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var user = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, DateTimeOffset.UtcNow);
        if (blocked)
            user.Block(DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task<Guid> SeedRevokedSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        session.Revoke(SessionRevokedReasonCodes.UserBlocked, DateTimeOffset.UtcNow);
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    private static UnblockUserCommand ValidCommand(Guid tenantId, Guid actorId, Guid targetUserId) => new(tenantId, actorId, targetUserId);

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_unblock_persists_the_status_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, blocked: true);
        using var services = await BuildServices();

        var result = await ExecuteUnblockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Active);

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == targetUserId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.UserUnblocked);
    }

    [Fact]
    public async Task Unblock_never_reactivates_a_session_revoked_before_the_block()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, blocked: true);
        var sessionId = await SeedRevokedSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteUnblockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Sessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync())
            .Should().Be(SessionStatus.Revoked); // still revoked — Unblock never restores it
    }

    // ---- Tests: rejections -------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteUnblockUserAsync(services, ValidCommand(tenantId, actorId, Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task A_user_of_a_different_tenant_is_indistinguishable_from_nonexistent()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantA);
        var userInTenantB = await SeedUserAsync(tenantB, blocked: true);
        using var services = await BuildServices();

        var result = await ExecuteUnblockUserAsync(services, ValidCommand(tenantA, actorId, userInTenantB));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Unblocking_an_already_active_user_fails_with_UserAlreadyActive_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteUnblockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserAlreadyActive);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_unblock_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, blocked: true);
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteUnblockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Blocked); // never unblocked
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after User.Unblock() was staged.");
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
