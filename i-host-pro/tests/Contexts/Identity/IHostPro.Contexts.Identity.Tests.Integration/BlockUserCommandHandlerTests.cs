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
/// End-to-end test of the BlockUser use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 7) — mirrors
/// <see cref="RemoveRoleCommandHandlerTests"/>'s structure, including the
/// same-shape real-concurrency proof for the last-Administrator guard
/// (Section 4: "duas transações simultâneas... apenas uma confirma"). This
/// file additionally proves the CROSS-COMMAND variant explicitly required by
/// Checkpoint 7: a concurrent BlockUser and RemoveRole(ADMIN) racing on the
/// two tenant's last Administrators must also let only one succeed — the
/// same <see cref="ILastAdministratorGuard"/> advisory lock key serializes
/// both, without any command-specific coordination code.
/// </summary>
public class BlockUserCommandHandlerTests : IClassFixture<BlockUserCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public BlockUserCommandHandlerTests(Fixture fixture)
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
        hostBuilder.Services.AddScoped<IBlockUserExecutor, BlockUserExecutor>();
        hostBuilder.Services.AddScoped<BlockUserCommandHandler>();
        // Only the cross-command concurrency test below needs RemoveRole
        // alongside BlockUser — registered unconditionally here anyway since
        // it costs nothing to have available and keeps BuildServices uniform
        // across every test in this file.
        hostBuilder.Services.AddScoped<IRemoveRoleExecutor, RemoveRoleExecutor>();
        hostBuilder.Services.AddScoped<RemoveRoleCommandHandler>();

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

    private static async Task<Result> ExecuteBlockUserAsync(
        IHost root, BlockUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new BlockUserCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IBlockUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteRemoveRoleAsync(
        IHost root, RemoveRoleCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IRemoveRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, cancellationToken).AsTask(),
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
    /// AssignRole/RemoveRole/ChangeOwnPassword/AdminResetPassword genuinely
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

    private static BlockUserCommand ValidCommand(Guid tenantId, Guid actorId, Guid targetUserId) => new(tenantId, actorId, targetUserId);

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_block_persists_the_status_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Blocked);

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == targetUserId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.UserBlocked);
        // Fase 12, Checkpoint 4 — the acting administrator, never the target, ends up in ActorId.
        auditEntries[0].ActorId.Should().Be(actorId);
        auditEntries[0].UserId.Should().Be(targetUserId);
    }

    /// <summary>
    /// Checkpoint 9 follow-up regression (mandatory per review) — see
    /// <see cref="AssignRoleCommandHandlerTests.A_valid_assignment_persists_the_targets_session_and_refresh_token_revocation"/>
    /// for the full rationale; this would have FAILED against the pre-fix
    /// <c>SessionReader</c>.
    /// </summary>
    [Fact]
    public async Task A_valid_block_persists_the_targets_session_and_refresh_token_revocation()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var (sessionId, refreshTokenId) = await SeedActiveSessionWithRefreshTokenAsync(tenantId, targetUserId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(SessionStatus.Revoked);
        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be(SessionRevokedReasonCodes.UserBlocked);

        var refreshToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.Id == refreshTokenId);
        refreshToken.IsRevoked.Should().BeTrue();
        refreshToken.RevokedAt.Should().NotBeNull();
        refreshToken.RevocationReason.Should().Be(RefreshTokenRevocationReason.UserBlocked);
    }

    [Fact]
    public async Task Blocking_an_Administrator_when_another_active_Administrator_exists_succeeds()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var otherAdminId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, otherAdminId, "ADMIN", actorId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Blocking_oneself_when_another_active_Administrator_remains_succeeds()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var otherAdminId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, actorId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, otherAdminId, "ADMIN", actorId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, actorId));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == actorId).Select(u => u.Status).SingleAsync()).Should().Be(UserStatus.Blocked);
    }

    // ---- Tests: rejections -------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, Guid.NewGuid()));

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

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantA, actorId, userInTenantB));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Blocking_an_already_blocked_user_fails_with_UserAlreadyBlocked_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, blocked: true);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserAlreadyBlocked);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0); // no new audit entry
    }

    [Fact]
    public async Task Blocking_the_tenants_last_active_Administrator_fails_with_LastActiveAdministrator_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Active); // untouched
    }

    [Fact]
    public async Task A_blocked_user_holding_ADMIN_does_not_count_as_an_active_Administrator()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        var blockedAdminId = await SeedUserAsync(tenantId, blocked: true);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, blockedAdminId, "ADMIN", actorId); // blocked — must not count
        using var services = await BuildServices();

        var result = await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);
    }

    // ---- Tests: real concurrency (Section 4) ---------------------------------

    /// <summary>
    /// Mirrors <see cref="RemoveRoleCommandHandlerTests.Two_concurrent_removals_of_the_two_last_Administrators_allow_only_one_to_succeed"/>
    /// exactly, for BlockUser instead of RemoveRole — see that test's doc
    /// comment for why sequential calls could never exercise the advisory
    /// lock at all.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_blocks_of_the_two_last_Administrators_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var adminA = await SeedUserAsync(tenantId);
        var adminB = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, adminA, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, adminB, "ADMIN", actorId);
        using var servicesA = await BuildServices();
        using var servicesB = await BuildServices();

        var taskA = ExecuteBlockUserAsync(servicesA, ValidCommand(tenantId, actorId, adminA));
        var taskB = ExecuteBlockUserAsync(servicesB, ValidCommand(tenantId, actorId, adminB));
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync(u => (u.Id == adminA || u.Id == adminB) && u.Status == UserStatus.Blocked))
            .Should().Be(1); // exactly one of the two ends up Blocked
    }

    /// <summary>
    /// The explicit Checkpoint 7 cross-command requirement (Section 4):
    /// blocking one of the tenant's two last Administrators and removing the
    /// ADMIN role from the other, run as genuinely simultaneous PostgreSQL
    /// transactions of TWO DIFFERENT command types, must still let only one
    /// confirm — proven here with zero coordination code beyond both
    /// handlers consulting the SAME <see cref="ILastAdministratorGuard"/>
    /// (same per-tenant advisory lock key).
    /// </summary>
    [Fact]
    public async Task Concurrent_BlockUser_and_RemoveRole_ADMIN_on_the_two_last_Administrators_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var adminA = await SeedUserAsync(tenantId);
        var adminB = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, adminA, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, adminB, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, adminB, "OPERATOR", actorId); // so RemoveRole(ADMIN) alone never hits UserMustHaveAtLeastOneRole
        using var servicesBlock = await BuildServices();
        using var servicesRemove = await BuildServices();

        var blockTask = ExecuteBlockUserAsync(servicesBlock, ValidCommand(tenantId, actorId, adminA));
        var removeTask = ExecuteRemoveRoleAsync(servicesRemove, new RemoveRoleCommand(tenantId, actorId, adminB, "ADMIN"));
        await Task.WhenAll(blockTask, removeTask);
        var blockResult = await blockTask;
        var removeResult = await removeTask;

        var successes = new[] { blockResult, removeResult }.Count(r => r.IsSuccess);
        successes.Should().Be(1, "only one of the two last Administrators may be invalidated");
        var failure = new[] { blockResult, removeResult }.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var adminAWasBlocked = (await dbContext.Users.Where(u => u.Id == adminA).Select(u => u.Status).SingleAsync()) == UserStatus.Blocked;
        var adminBLostAdminRole = !await dbContext.UserRoles.AnyAsync(ur => ur.UserId == adminB && ur.RoleCode == "ADMIN");
        (adminAWasBlocked == blockResult.IsSuccess).Should().BeTrue();
        (adminBLostAdminRole == removeResult.IsSuccess).Should().BeTrue();
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_block_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteBlockUserAsync(services, ValidCommand(tenantId, actorId, targetUserId));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
            .Should().Be(UserStatus.Active); // never blocked
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after User.Block() was staged.");
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
