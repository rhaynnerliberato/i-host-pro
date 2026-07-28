using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Etapa 15A follow-up (approved with reservations): dedicated coverage of
/// <see cref="LoginTenantAwareBehavior"/> delegating to
/// <see cref="IIdentityTransactionExecutor"/>, independent of
/// <see cref="AuthEndpointsTests"/> (which exercises Login only indirectly,
/// through HTTP). <see cref="LoginTenantAwareBehavior"/> is constructed
/// directly here (its three constructor dependencies resolved from a real DI
/// scope, mirroring how every other test file in this project manually
/// replicates a pipeline step instead of resolving it through Mediator) and
/// invoked with a <c>next</c> delegate that runs the REAL
/// <see cref="LoginCommandHandler"/>, from the SAME scope (so it shares the
/// same <see cref="IdentityDbContext"/> the executor later flushes), and also
/// stages a <see cref="CanaryIntegrationEvent"/> on the real
/// <see cref="IIntegrationEventCollector"/> — standing in for what a real
/// Integration Event publication will do once the six events exist (out of
/// scope here). This proves the domain writes a real login produces (User,
/// Session, RefreshToken, SecurityAuditEntry) commit atomically with the
/// outbox envelope, and that an exception rolls back all of it — without
/// depending on any other test file for the primary proof.
/// </summary>
public class LoginTenantAwareBehaviorTests : IClassFixture<LoginTenantAwareBehaviorTests.Fixture>
{
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string OutboxSchema = "identity_messaging";

    private readonly Fixture _fixture;

    public LoginTenantAwareBehaviorTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer PostgresContainer { get; private set; } = null!;
        public RabbitMqContainer RabbitMqContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            PostgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            RabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();

            await Task.WhenAll(PostgresContainer.StartAsync(), RabbitMqContainer.StartAsync());

            var adminConnectionString = PostgresContainer.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
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
            await PostgresContainer.DisposeAsync();
            await RabbitMqContainer.DisposeAsync();
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
            await using var grantCommand = connection.CreateCommand();
            grantCommand.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await grantCommand.ExecuteNonQueryAsync();
        }
    }

    // ---- Service graph -----------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _fixture.AppConnectionString,
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
        hostBuilder.Services.AddScoped<LoginCommandHandler>();

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = _fixture.RabbitMqContainer.Hostname;
                rabbit.Port = _fixture.RabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
            opts.PublishMessage(typeof(CanaryIntegrationEvent)).ToRabbitExchange("identity-events-test").UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
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

    private async Task<(Guid UserId, string Email)> SeedUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var email = $"{Guid.NewGuid():N}@ihostpro.com";

        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (user.Id, email);
    }

    private async Task<string> GetTenantSlugAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        return tenant.Slug.Value;
    }

    private static LoginCommand LoginAs(string tenantSlug, string email, string password = KnownPassword) =>
        new(tenantSlug, email, password, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

    private static CanaryIntegrationEvent NewCanaryEvent(Guid tenantId, Guid userId) => new()
    {
        TenantId = tenantId,
        AggregateId = userId,
        AggregateType = "User",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = userId.ToString(),
        Marker = "login-canary",
    };

    private async Task<long> CountOutgoingEnvelopesAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public async Task Successful_login_persists_User_Session_RefreshToken_audit_and_the_canary_envelope_atomically()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        var command = LoginAs(slug, email);

        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var behavior = new LoginTenantAwareBehavior(
            sp.GetRequiredService<ITenantBootstrapResolver<LoginCommand>>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IIdentityTransactionExecutor>());
        var collector = sp.GetRequiredService<IIntegrationEventCollector>();
        var handler = sp.GetRequiredService<LoginCommandHandler>();
        var envelopesBefore = await CountOutgoingEnvelopesAsync();

        // Broker paused so the envelope stays visible in the outgoing table
        // long enough to count it reliably, instead of racing the Durability
        // Agent's near-immediate flush when RabbitMQ is reachable (Etapa 15A
        // foundation tests hit this same raciness — see IdentityOutboxTransactionExecutorTests).
        await _fixture.RabbitMqContainer.StopAsync();
        Result<AuthTokensResult> result;
        try
        {
            result = await behavior.Handle(command, async (msg, ct) =>
            {
                var handlerResult = await handler.Handle(msg, ct);
                handlerResult.IsSuccess.Should().BeTrue();
                collector.Enqueue(NewCanaryEvent(tenantId, userId));
                return handlerResult;
            }, CancellationToken.None);
        }
        finally
        {
            await _fixture.RabbitMqContainer.StartAsync();
        }

        result.IsSuccess.Should().BeTrue();

        await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(verifyDbContext, tenantId);

        var user = await verifyDbContext.Users.SingleAsync(u => u.Id == userId);
        user.LastLoginAt.Should().NotBeNull();
        (await verifyDbContext.Sessions.CountAsync(s => s.UserId == userId)).Should().Be(1);
        (await verifyDbContext.RefreshTokens.CountAsync(rt => rt.UserId == userId)).Should().Be(1);
        (await verifyDbContext.SecurityAuditLog.CountAsync(a => a.UserId == userId)).Should().Be(1);
        (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore + 1);
    }

    [Fact]
    public async Task An_exception_after_a_successful_login_rolls_back_the_domain_writes_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        var command = LoginAs(slug, email);

        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var behavior = new LoginTenantAwareBehavior(
            sp.GetRequiredService<ITenantBootstrapResolver<LoginCommand>>(),
            sp.GetRequiredService<ITenantContext>(),
            sp.GetRequiredService<IIdentityTransactionExecutor>());
        var collector = sp.GetRequiredService<IIntegrationEventCollector>();
        var handler = sp.GetRequiredService<LoginCommandHandler>();
        var envelopesBefore = await CountOutgoingEnvelopesAsync();

        var act = () => behavior.Handle(command, async (msg, ct) =>
        {
            // The real handler stages User/Session/RefreshToken/audit changes
            // exactly as in the successful-path test above — nothing here
            // must survive the exception thrown right after.
            var handlerResult = await handler.Handle(msg, ct);
            handlerResult.IsSuccess.Should().BeTrue();
            collector.Enqueue(NewCanaryEvent(tenantId, userId));
            throw new InvalidOperationException("Simulated failure after Login staged its writes.");
        }, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(verifyDbContext, tenantId);

        var user = await verifyDbContext.Users.SingleAsync(u => u.Id == userId);
        user.LastLoginAt.Should().BeNull(); // never committed
        (await verifyDbContext.Sessions.CountAsync(s => s.UserId == userId)).Should().Be(0);
        (await verifyDbContext.RefreshTokens.CountAsync(rt => rt.UserId == userId)).Should().Be(0);
        (await verifyDbContext.SecurityAuditLog.CountAsync(a => a.UserId == userId)).Should().Be(0);
        (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
    }

    // ---- Helpers ------------------------------------------------------------

    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }
}
