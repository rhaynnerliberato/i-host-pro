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
/// End-to-end test of the ChangeOwnPassword and AdminResetPassword use cases
/// against a real PostgreSQL instance (Incremento 3, Checkpoint 9) — mirrors
/// <see cref="UpdateUserCommandHandlerTests"/>'s structure: real Argon2
/// hashing/verification (never a stub), real session/refresh-token cascade
/// persistence, and the same genuine <c>xmin</c>-based optimistic concurrency
/// proof used for UpdateUser, since Section 8 of the Checkpoint 9 decision
/// requires the identical no-retry translation for both new commands.
/// Session-revocation-cache (Redis) writes are exercised at the HTTP level by
/// <see cref="ChangePasswordEndpointsTests"/>, not here — mirrors
/// <see cref="BlockUserCommandHandlerTests"/>'s scope split.
/// </summary>
public class ChangePasswordCommandHandlerTests : IClassFixture<ChangePasswordCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string NewPassword = "New-Correct-Horse-Battery-Staple-43!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ChangePasswordCommandHandlerTests(Fixture fixture)
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
        hostBuilder.Services.AddScoped<IChangeOwnPasswordExecutor, ChangeOwnPasswordExecutor>();
        hostBuilder.Services.AddScoped<ChangeOwnPasswordCommandHandler>();
        hostBuilder.Services.AddScoped<IAdminResetPasswordExecutor, AdminResetPasswordExecutor>();
        hostBuilder.Services.AddScoped<AdminResetPasswordCommandHandler>();

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

    private static async Task<Result> ExecuteChangeOwnPasswordAsync(
        IHost root, ChangeOwnPasswordCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new ChangeOwnPasswordCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IChangeOwnPasswordExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<ChangeOwnPasswordCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteAdminResetPasswordAsync(
        IHost root, AdminResetPasswordCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new AdminResetPasswordCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IAdminResetPasswordExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<AdminResetPasswordCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    private async Task<Guid> SeedActiveSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, device: null, browser: null, ipAddress: null);
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    // ---- Tests: ChangeOwnPassword happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_own_password_change_persists_the_new_hash_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, NewPassword));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var persistedUser = await dbContext.Users.Where(u => u.Id == userId).SingleAsync();
        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        hasher.VerifyHashedPassword(null!, persistedUser.PasswordHash.Value, NewPassword)
            .Should().NotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
        hasher.VerifyHashedPassword(null!, persistedUser.PasswordHash.Value, KnownPassword)
            .Should().Be(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed); // old password no longer works

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == userId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.PasswordChangedBySelf);
    }

    [Fact]
    public async Task A_valid_own_password_change_revokes_every_active_session_including_the_current_one()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        var currentSessionId = await SeedActiveSessionAsync(tenantId, userId);
        var otherSessionId = await SeedActiveSessionAsync(tenantId, userId);
        using var services = await BuildServices();

        var result = await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, NewPassword));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var sessions = await dbContext.Sessions.Where(s => s.Id == currentSessionId || s.Id == otherSessionId).ToListAsync();
        sessions.Should().OnlyContain(s => s.Status == SessionStatus.Revoked);
    }

    // ---- Tests: ChangeOwnPassword rejections -------------------------------------------------

    [Fact]
    public async Task An_incorrect_current_password_fails_with_InvalidCurrentPassword_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, "wrong-password", NewPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.InvalidCurrentPassword);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task A_new_password_equal_to_the_current_one_fails_with_NewPasswordMustDiffer_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, KnownPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.NewPasswordMustDiffer);
    }

    [Fact]
    public async Task A_blocked_authenticated_user_fails_with_AuthenticatedUserNotFound()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, blocked: true);
        using var services = await BuildServices();

        var result = await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, NewPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.AuthenticatedUserNotFound);
    }

    // ---- Tests: AdminResetPassword happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_admin_reset_persists_the_new_hash_and_one_audit_entry_with_PasswordResetByAdmin()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantId, actorId, targetUserId, NewPassword));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == targetUserId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.PasswordResetByAdmin);
        // Fase 12, Checkpoint 4 — the acting administrator, never the target, ends up in ActorId.
        auditEntries[0].ActorId.Should().Be(actorId);
        auditEntries[0].UserId.Should().Be(targetUserId);
    }

    [Fact]
    public async Task Resetting_a_blocked_users_password_succeeds_without_unblocking_them()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, blocked: true);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantId, actorId, targetUserId, NewPassword));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Blocked); // never unblocked
    }

    [Fact]
    public async Task Resetting_a_targets_password_revokes_every_active_session_of_the_target()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var targetSessionId = await SeedActiveSessionAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantId, actorId, targetUserId, NewPassword));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Sessions.Where(s => s.Id == targetSessionId).Select(s => s.Status).SingleAsync())
            .Should().Be(SessionStatus.Revoked);
    }

    // ---- Tests: AdminResetPassword rejections -------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantId, actorId, Guid.NewGuid(), NewPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task A_user_of_a_different_tenant_is_indistinguishable_from_nonexistent()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantA);
        var userInTenantB = await SeedUserAsync(tenantB);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantA, actorId, userInTenantB, NewPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task An_Administrator_resetting_their_own_password_fails_with_AdminCannotResetOwnPassword_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteAdminResetPasswordAsync(
            services, new AdminResetPasswordCommand(tenantId, actorId, actorId, NewPassword));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.AdminCannotResetOwnPassword);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == actorId)).Should().Be(0);
    }

    // ---- Tests: real concurrency (Section 8) ---------------------------------

    /// <summary>Mirrors <see cref="UpdateUserCommandHandlerTests.Two_concurrent_updates_of_the_same_user_allow_only_one_to_succeed"/> exactly — see its doc comment for the barrier rationale.</summary>
    private sealed class BarrierSecurityAuditWriter : ISecurityAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierSecurityAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(SecurityAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_admin_resets_of_the_same_target_allow_only_one_to_succeed_and_translate_to_UserConcurrencyConflict_without_retry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var barrier = new Barrier(2);
        using var servicesA = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter>(_ => new BarrierSecurityAuditWriter(barrier)));
        using var servicesB = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter>(_ => new BarrierSecurityAuditWriter(barrier)));

        var taskA = ExecuteAdminResetPasswordAsync(servicesA, new AdminResetPasswordCommand(tenantId, actorId, targetUserId, "Password-A-42!"));
        var taskB = ExecuteAdminResetPasswordAsync(servicesB, new AdminResetPasswordCommand(tenantId, actorId, targetUserId, "Password-B-42!"));
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(IdentityErrorCodes.UserConcurrencyConflict);
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_password_change_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId);
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteChangeOwnPasswordAsync(
            services, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, NewPassword));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var persistedUser = await dbContext.Users.Where(u => u.Id == userId).SingleAsync();
        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        hasher.VerifyHashedPassword(null!, persistedUser.PasswordHash.Value, KnownPassword)
            .Should().NotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed); // old password still works
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after User.SetPasswordHash() was staged.");
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
