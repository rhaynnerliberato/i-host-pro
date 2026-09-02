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
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Identity;
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
/// End-to-end test of the CreateUser use case against a real PostgreSQL
/// instance (Incremento 3, Checkpoint 5) — mirrors <see cref="RevokeOwnSessionCommandHandlerTests"/>'s
/// structure: manually chained the way <c>CreateUserTenantAwareBehavior</c>
/// would, tenant/actor already resolved from an authenticated Administrator's
/// claims. Focuses on PostgreSQL-observable state (User/UserRole/audit rows,
/// the real unique-constraint race); event CONTENT assertions
/// (UserCreated/UserRoleAssigned payloads, CausationId chaining, ActorId, no
/// event on rollback) live in <see cref="IdentityIntegrationEventsTests"/>
/// alongside the other Identity events, not here.
/// </summary>
public class CreateUserCommandHandlerTests : IClassFixture<CreateUserCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public CreateUserCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale. Also provisions Identity's outbox, mirroring
    /// <see cref="RevokeOwnSessionCommandHandlerTests.Fixture"/>, since
    /// <see cref="ICreateUserExecutor"/> depends on
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
        hostBuilder.Services.AddScoped<ICreateUserExecutor, CreateUserExecutor>();
        hostBuilder.Services.AddScoped<CreateUserCommandHandler>();

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();

        // Required now that CreateUserCommandHandler publishes Integration
        // Events: IDbContextOutbox<T>.PublishAsync throws
        // WolverineHasNotStartedException against a host that was only
        // Build() and never started — confirmed empirically for the
        // equivalent Logout/RevokeOwnSession tests.
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Manually replicates ValidationBehavior -> CreateUserTenantAwareBehavior -> handler:
    /// tenant/actor are already resolved (simulating an authenticated
    /// Administrator's claims), never derived from anything client-supplied.
    /// </summary>
    private static async Task<Result<UserResult>> ExecuteCreateUserAsync(
        IHost root, CreateUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new CreateUserCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<UserResult>(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<ICreateUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<CreateUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    /// <summary>
    /// <see cref="UserRole.AssignedByUserId"/> carries a real foreign key to
    /// <c>users(tenant_id, id)</c> — confirmed empirically
    /// (FK_user_roles_users_tenant_id_assigned_by_user_id) — so every test's
    /// ActorId must be a genuinely persisted user of that same tenant, never
    /// an arbitrary <c>Guid.NewGuid()</c> (which production never produces
    /// either: the actor is always an already-authenticated Administrator).
    /// </summary>
    private async Task<Guid> SeedActorUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, "Correct-Horse-Battery-Staple-42!"));
        var actor = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Admin Actor", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(actor);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return actor.Id;
    }

    private static CreateUserCommand ValidCommand(Guid tenantId, Guid actorId, string? email = null) => new(
        tenantId, actorId, "Test User", email ?? $"{Guid.NewGuid():N}@ihostpro.com",
        "Correct-Horse-Battery-Staple-42!", "ADMIN");

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task A_valid_command_persists_the_user_and_exactly_one_role_and_two_audit_entries()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var command = ValidCommand(tenantId, actorId);

        var result = await ExecuteCreateUserAsync(services, command);

        result.IsSuccess.Should().BeTrue();
        result.Value.FullName.Should().Be("Test User");
        result.Value.Status.Should().Be("Active");
        result.Value.Roles.Should().Equal("ADMIN");

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var user = await dbContext.Users.SingleAsync(u => u.Id == result.Value.Id);
        user.Status.Should().Be(UserStatus.Active);

        var userRoles = await dbContext.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync();
        userRoles.Should().ContainSingle();
        userRoles[0].RoleCode.Should().Be("ADMIN");
        userRoles[0].AssignedByUserId.Should().Be(actorId);

        var auditEntries = await dbContext.SecurityAuditLog.Where(e => e.UserId == user.Id).ToListAsync();
        auditEntries.Should().HaveCount(2);
        auditEntries.Should().Contain(e => e.EventType == SecurityAuditEventType.UserCreated);
        auditEntries.Should().Contain(e => e.EventType == SecurityAuditEventType.UserRoleAssigned);
        // Fase 12, Checkpoint 4 — the acting administrator, never the newly
        // created user itself, ends up in ActorId on both entries.
        auditEntries.Should().OnlyContain(e => e.ActorId == actorId);
    }

    [Fact]
    public async Task The_email_is_persisted_normalized_via_the_real_Email_value_object()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var command = ValidCommand(tenantId, actorId, email: "Mixed.Case@IHostPro.com");

        var result = await ExecuteCreateUserAsync(services, command);
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var user = await dbContext.Users.SingleAsync(u => u.Id == result.Value.Id);
        user.NormalizedEmail.Should().Be(Email.Create("Mixed.Case@IHostPro.com").NormalizedValue);
        user.Email.Value.Should().Be("Mixed.Case@IHostPro.com"); // display form preserved as submitted
    }

    [Fact]
    public async Task The_password_is_hashed_via_the_real_Argon2_hasher_never_stored_in_plain_text()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        const string password = "Correct-Horse-Battery-Staple-42!";
        var command = ValidCommand(tenantId, actorId) with { InitialPassword = password };

        var result = await ExecuteCreateUserAsync(services, command);
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var user = await dbContext.Users.SingleAsync(u => u.Id == result.Value.Id);
        user.PasswordHash.Value.Should().NotBe(password);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        hasher.VerifyHashedPassword(user, user.PasswordHash.Value, password).Should().Be(PasswordVerificationResult.Success);
    }

    // ---- Tests: role not found ------------------------------------------------

    [Fact]
    public async Task A_nonexistent_role_code_fails_with_RoleNotFound_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var command = ValidCommand(tenantId, actorId) with { RoleCode = "NOT_A_REAL_ROLE" };

        var result = await ExecuteCreateUserAsync(services, command);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleNotFound);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync()).Should().Be(1);
        (await dbContext.SecurityAuditLog.CountAsync()).Should().Be(0);
    }

    // ---- Tests: invalid email format -------------------------------------------

    [Fact]
    public async Task An_invalid_email_format_fails_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var command = ValidCommand(tenantId, actorId) with { Email = "not-an-email" };

        var result = await ExecuteCreateUserAsync(services, command);

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync()).Should().Be(1);
    }

    // ---- Tests: invalid password policy -----------------------------------

    [Fact]
    public async Task A_password_that_fails_the_configured_policy_fails_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        // Argon2Options/PasswordPolicyOptions bound with no configuration
        // section supplied resolve to their type defaults — RequiredLength
        // defaults to a positive minimum, so an empty-after-validator string
        // would already be rejected by CreateUserCommandValidator; a single
        // very short, non-empty password instead exercises the POLICY
        // rejection path specifically (PasswordPolicyValidator, not
        // NotEmpty()).
        var command = ValidCommand(tenantId, actorId) with { InitialPassword = "a" };

        var result = await ExecuteCreateUserAsync(services, command);

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync()).Should().Be(1);
    }

    // ---- Tests: email uniqueness -------------------------------------------

    [Fact]
    public async Task Two_concurrent_creations_with_the_same_email_in_the_same_tenant_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var email = $"{Guid.NewGuid():N}@ihostpro.com";
        var commandA = ValidCommand(tenantId, actorId, email);
        var commandB = ValidCommand(tenantId, actorId, email);

        var taskA = ExecuteCreateUserAsync(services, commandA);
        var taskB = ExecuteCreateUserAsync(services, commandB);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(IdentityErrorCodes.EmailAlreadyInUse);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync(u => u.NormalizedEmail == email.ToLowerInvariant())).Should().Be(1);
    }

    [Fact]
    public async Task The_same_email_in_a_different_tenant_is_allowed()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorA = await SeedActorUserAsync(tenantA);
        var actorB = await SeedActorUserAsync(tenantB);
        using var services = await BuildServices();
        var email = $"{Guid.NewGuid():N}@ihostpro.com";

        var resultA = await ExecuteCreateUserAsync(services, ValidCommand(tenantA, actorA, email));
        var resultB = await ExecuteCreateUserAsync(services, ValidCommand(tenantB, actorB, email));

        resultA.IsSuccess.Should().BeTrue();
        resultB.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Differing_only_by_case_in_the_same_tenant_is_a_conflict()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices();
        var email = $"{Guid.NewGuid():N}@ihostpro.com";

        var first = await ExecuteCreateUserAsync(services, ValidCommand(tenantId, actorId, email));
        first.IsSuccess.Should().BeTrue();

        var second = await ExecuteCreateUserAsync(services, ValidCommand(tenantId, actorId, email.ToUpperInvariant()));

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be(IdentityErrorCodes.EmailAlreadyInUse);
    }

    // ---- Tests: rollback ----------------------------------------------------

    [Fact]
    public async Task A_failure_after_the_user_and_role_were_staged_rolls_back_the_entire_transaction()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var services = await BuildServices(overrides: sc =>
            sc.AddScoped<ISecurityAuditWriter, ThrowingSecurityAuditWriter>());
        var command = ValidCommand(tenantId, actorId);

        var act = async () => await ExecuteCreateUserAsync(services, command);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Users.CountAsync()).Should().Be(1);
        (await dbContext.UserRoles.CountAsync()).Should().Be(0);
    }

    private sealed class ThrowingSecurityAuditWriter : ISecurityAuditWriter
    {
        public void Record(SecurityAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after User/UserRole were staged.");
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
