using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// Validates the two Ownership Integration Events'
/// (<see cref="PropertyOwnerLinked"/>/<see cref="PropertyOwnerUnlinked"/>)
/// durable outbox end-to-end against real PostgreSQL and RabbitMQ
/// (Testcontainers) — mirrors <see cref="PropertyLifecycleIntegrationEventsTests"/>
/// exactly, with the addition that (like <see cref="PropertyOwnerCommandHandlerTests"/>)
/// the <c>identity</c> schema and Identity's module are also provisioned/
/// registered, since Link genuinely calls Identity's eligibility reader.
/// </summary>
public class PropertyOwnerIntegrationEventsTests : IClassFixture<PropertyOwnerIntegrationEventsTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";
    private const string ExchangeName = "property-management-events-test";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertyOwnerIntegrationEventsTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

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

            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
            {
                await identityDbContext.Database.MigrateAsync();
            }
            await using (var pmDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await pmDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
            await ProvisionIdentityMessagingAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;

            return new IdentityDbContext(options, new TenantContext());
        }

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(PropertyManagementDbContext));
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

        private async Task ProvisionIdentityMessagingAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, "identity_messaging", typeof(PropertyManagementDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();
        }
    }

    private static readonly PropertyAddressInput SomeAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    // ---- Real Api-equivalent host (both modules + Wolverine/RabbitMQ) -----------

    private async Task<IHost> BuildHostAsync(RabbitMqContainer rabbitMqContainer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = _signingKeyPem,
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
        hostBuilder.Services.AddPropertyManagementModule(configuration);
        hostBuilder.Services.AddPropertyManagementCommandDispatch();

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });

            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(PropertyManagementDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(1);
            opts.Durability.FirstHealthCheckExecution = TimeSpan.FromSeconds(1);
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(5);

            opts.PublishMessage(typeof(PropertyOwnerLinked))
                .ToRabbitRoutingKey(ExchangeName, "property_owner_linked", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
            opts.PublishMessage(typeof(PropertyOwnerUnlinked))
                .ToRabbitRoutingKey(ExchangeName, "property_owner_unlinked", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Result<TResponse>> ExecuteAsync<TMessage, TResponse>(IHost host, TMessage message, Guid tenantId)
        where TMessage : IRequest<Result<TResponse>>
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    private static async Task<Result> ExecuteAsync<TMessage>(IHost host, TMessage message, Guid tenantId)
        where TMessage : IRequest<Result>
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    private static async Task<Guid> SeedPropertyAsync(IHost host, Guid tenantId, string code)
    {
        var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), code, $"Property {code}", 2, null, SomeAddress);
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);
        return created.Value.Id;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenantId;
    }

    private async Task<Guid> SeedEligibleOwnerAsync(Guid tenantId)
    {
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test Owner", hash, now);
        dbContext.Users.Add(user);
        dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, IdentityRoleCodes.PropertyOwner, now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private IdentityDbContext CreateIdentityDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Tests: same-commit envelope -----------------------------------------

    [Fact]
    public async Task Linking_an_owner_persists_a_PropertyOwnerLinked_envelope_in_property_management_messaging_only()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = await SeedTenantAsync();
            var ownerId = await SeedEligibleOwnerAsync(tenantId);
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var propertyId = await SeedPropertyAsync(host, tenantId, "OE-1");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
                    host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                result.IsSuccess.Should().BeTrue();

                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerLinked")).Should().Be(1);
                (await CountEnvelopesAsync("identity_messaging", "IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerLinked")).Should().Be(0);
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unlinking_an_owner_persists_a_PropertyOwnerUnlinked_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = await SeedTenantAsync();
            var ownerId = await SeedEligibleOwnerAsync(tenantId);
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var propertyId = await SeedPropertyAsync(host, tenantId, "OE-2");
                await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
                    host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                result.IsSuccess.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerUnlinked")).Should().Be(1);
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_rejected_link_attempt_never_publishes_an_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = await SeedTenantAsync();
            var ownerId = await SeedEligibleOwnerAsync(tenantId);
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var propertyId = await SeedPropertyAsync(host, tenantId, "OE-3");
                await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
                    host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                await rabbitMqContainer.StopAsync();

                // Already linked — this second attempt is rejected.
                var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
                    host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                result.IsFailure.Should().BeTrue();
                // The first, successful link's envelope was already delivered and
                // removed from the outgoing table while RabbitMQ was still up —
                // mirrors PropertyLifecycleIntegrationEventsTests' equivalent
                // assertion. The rejected second attempt adds nothing.
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerLinked")).Should().Be(0);
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_rejected_unlink_attempt_never_publishes_an_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = await SeedTenantAsync();
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var propertyId = await SeedPropertyAsync(host, tenantId, "OE-4");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, Guid.NewGuid()), tenantId);

                result.IsFailure.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerUnlinked")).Should().Be(0);
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    // ---- Tests: RabbitMQ outage / recovery ------------------------------------

    [Fact]
    public async Task RabbitMQ_unavailable_does_not_block_the_link_commit_and_the_envelope_is_delivered_after_recovery()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = await SeedTenantAsync();
            var ownerId = await SeedEligibleOwnerAsync(tenantId);
            Guid propertyId;

            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                propertyId = await SeedPropertyAsync(host, tenantId, "OE-5");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
                    host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

                result.IsSuccess.Should().BeTrue();

                (await EnvelopeIsPendingAsync(propertyId)).Should().BeTrue();
            }
            finally
            {
                await StopGracefullyAsync(host);
            }

            await rabbitMqContainer.StartAsync();

            var recoveryHost = await BuildHostAsync(rabbitMqContainer);
            try
            {
                await WaitUntilAsync(async () => !await EnvelopeIsPendingAsync(propertyId), TimeSpan.FromSeconds(30));
            }
            finally
            {
                await StopGracefullyAsync(recoveryHost);
            }

            (await CountDeadLettersAsync()).Should().Be(0);
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<long> CountEnvelopesAsync(string schema, string messageType)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_outgoing_envelopes WHERE message_type = @messageType";
        command.Parameters.AddWithValue("messageType", messageType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountDeadLettersAsync()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {OutboxSchema}.wolverine_dead_letters";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> EnvelopeIsPendingAsync(Guid propertyId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type = 'IHostPro.Contexts.PropertyManagement.Contracts.PropertyOwnerLinked'
              AND position(convert_to(@propertyId, 'UTF8') in body) > 0
            """;
        command.Parameters.AddWithValue("propertyId", propertyId.ToString());
        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private static async Task StopGracefullyAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static PropertyManagementDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
