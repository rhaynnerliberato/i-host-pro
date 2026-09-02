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
/// End-to-end test of the UpdateUser use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 8) — mirrors
/// <see cref="RemoveRoleCommandHandlerTests"/>'s structure. In addition to
/// the atomicity/idempotency/business-rule coverage that class establishes,
/// this file also proves the genuine <c>xmin</c>-based optimistic
/// concurrency conflict (Section 6) — the one guarantee that requires two
/// simultaneous PostgreSQL transactions racing on the SAME row's UPDATE,
/// relying on Postgres's own row-level locking (not an advisory lock) to
/// serialize them: whichever transaction's <c>UPDATE ... WHERE xmin = @v1</c>
/// commits first wins; the second, unblocked only after the first commits,
/// finds zero matching rows and EF Core raises
/// <see cref="DbUpdateConcurrencyException"/>.
/// </summary>
public class UpdateUserCommandHandlerTests : IClassFixture<UpdateUserCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public UpdateUserCommandHandlerTests(Fixture fixture)
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
        hostBuilder.Services.AddScoped<IUpdateUserExecutor, UpdateUserExecutor>();
        hostBuilder.Services.AddScoped<UpdateUserCommandHandler>();

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

    private static async Task<Result<UserResult>> ExecuteUpdateUserAsync(
        IHost root, UpdateUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new UpdateUserCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<UserResult>(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IUpdateUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<UpdateUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    private async Task<Guid> SeedUserAsync(Guid tenantId, string? fullName = null, string? email = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var user = User.Register(
            Guid.NewGuid(), tenantId, Email.Create(email ?? $"{Guid.NewGuid():N}@ihostpro.com"),
            fullName ?? "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_full_name_update_persists_it_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, fullName: "Original Name");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(services, new UpdateUserCommand(tenantId, actorId, targetUserId, "New Name", null));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.FullName).SingleAsync()).Should().Be("New Name");

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == targetUserId).ToListAsync();
        auditEntries.Should().ContainSingle();
        auditEntries[0].EventType.Should().Be(SecurityAuditEventType.UserUpdated);
        // Fase 12, Checkpoint 4 — the acting administrator, never the target, ends up in ActorId.
        auditEntries[0].ActorId.Should().Be(actorId);
        auditEntries[0].UserId.Should().Be(targetUserId);
    }

    [Fact]
    public async Task A_valid_email_update_persists_it_and_one_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, email: "original@ihostpro.com");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, null, "updated@ihostpro.com"));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var persisted = await dbContext.Users.Where(u => u.Id == targetUserId).SingleAsync();
        persisted.NormalizedEmail.Should().Be("updated@ihostpro.com");

        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(1);
    }

    // ---- Tests: idempotency (Section 4) ------------------------------------

    [Fact]
    public async Task A_no_op_request_persists_nothing_and_creates_no_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, fullName: "Same Name");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, "Same Name", null));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
    }

    [Fact]
    public async Task Same_email_with_different_casing_is_a_no_op()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, email: "same@ihostpro.com");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, null, "SAME@IHostPro.com"));

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
    }

    // ---- Tests: rejections -------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, Guid.NewGuid(), "New Name", null));

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

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantA, actorId, userInTenantB, "New Name", null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Email_already_used_by_another_user_of_the_same_tenant_fails_with_EmailAlreadyInUse_and_changes_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        await SeedUserAsync(tenantId, email: "taken@ihostpro.com");
        var targetUserId = await SeedUserAsync(tenantId, fullName: "Original Name", email: "target@ihostpro.com");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, null, "taken@ihostpro.com"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.EmailAlreadyInUse);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.NormalizedEmail).SingleAsync())
            .Should().Be("target@ihostpro.com"); // untouched
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
    }

    [Fact]
    public async Task Casing_difference_of_another_users_email_still_conflicts()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        await SeedUserAsync(tenantId, email: "taken@ihostpro.com");
        var targetUserId = await SeedUserAsync(tenantId, email: "target@ihostpro.com");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, null, "TAKEN@IHostPro.com"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.EmailAlreadyInUse);
    }

    [Fact]
    public async Task The_same_email_in_a_different_tenant_is_allowed()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorA = await SeedUserAsync(tenantA);
        await SeedUserAsync(tenantB, email: "shared@ihostpro.com");
        var targetInTenantA = await SeedUserAsync(tenantA, email: "target@ihostpro.com");
        using var services = await BuildServices();

        var result = await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantA, actorA, targetInTenantA, null, "shared@ihostpro.com"));

        result.IsSuccess.Should().BeTrue();
    }

    // ---- Tests: real concurrency (Section 6) ---------------------------------

    /// <summary>
    /// Forces the genuine overlap a bare <see cref="Task.WhenAll(Task[])"/>
    /// does NOT guarantee: without it, one transaction's full read-mutate-commit
    /// cycle can complete before the other's read even runs, so both simply
    /// succeed sequentially against different <c>xmin</c> generations instead
    /// of racing (confirmed empirically — the first version of this test,
    /// without a barrier, flaked as "2 successes" instead of "1 success, 1
    /// conflict"). <see cref="ISecurityAuditWriter.Record"/> runs AFTER both
    /// handlers have already read the row and mutated their in-memory copy,
    /// but BEFORE either's <c>SaveChangesAndFlushMessagesAsync</c> — the exact
    /// point that must block until the second participant arrives, so both
    /// transactions are guaranteed to have read the SAME <c>xmin</c> before
    /// either is allowed to commit.
    /// </summary>
    private sealed class BarrierSecurityAuditWriter : ISecurityAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierSecurityAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(SecurityAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_user_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, fullName: "Original Name");
        using var barrier = new Barrier(2);
        using var servicesA = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter>(_ => new BarrierSecurityAuditWriter(barrier)));
        using var servicesB = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter>(_ => new BarrierSecurityAuditWriter(barrier)));

        var taskA = ExecuteUpdateUserAsync(servicesA, new UpdateUserCommand(tenantId, actorId, targetUserId, "Name A", null));
        var taskB = ExecuteUpdateUserAsync(servicesB, new UpdateUserCommand(tenantId, actorId, targetUserId, "Name B", null));
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(IdentityErrorCodes.UserConcurrencyConflict);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var finalName = await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.FullName).SingleAsync();
        finalName.Should().BeOneOf("Name A", "Name B"); // exactly the winner's value, never a merge of both
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_update_was_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId, fullName: "Original Name");
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());

        var act = async () => await ExecuteUpdateUserAsync(
            services, new UpdateUserCommand(tenantId, actorId, targetUserId, "New Name", null));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.FullName).SingleAsync())
            .Should().Be("Original Name"); // never updated
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after User.ChangeFullName() was staged.");
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
