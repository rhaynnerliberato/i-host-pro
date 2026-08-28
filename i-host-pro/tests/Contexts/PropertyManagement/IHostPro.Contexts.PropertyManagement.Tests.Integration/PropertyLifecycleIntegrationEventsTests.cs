using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
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
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// Validates the three lifecycle Integration Events' durable outbox
/// end-to-end against real PostgreSQL and RabbitMQ (Testcontainers) —
/// mirrors <c>PropertyIntegrationEventsTests</c> exactly: same-commit
/// envelope persistence, schema isolation from <c>identity_messaging</c>,
/// broker outage not blocking commit, lossless delivery after recovery
/// (Checkpoint 4 plan, item 17). Dispatches through the REAL production
/// composition root via <see cref="ISender"/>.
/// </summary>
public class PropertyLifecycleIntegrationEventsTests : IClassFixture<PropertyLifecycleIntegrationEventsTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";
    private const string ExchangeName = "property-management-events-test";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PropertyLifecycleIntegrationEventsTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
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

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
            await ProvisionIdentityMessagingAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

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

    // ---- Real Api-equivalent host (AddPropertyManagementCommandDispatch + Wolverine) ----

    private async Task<IHost> BuildHostAsync(RabbitMqContainer rabbitMqContainer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);
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

            opts.PublishMessage(typeof(PropertyActivated))
                .ToRabbitRoutingKey(ExchangeName, "property_activated", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
            opts.PublishMessage(typeof(PropertyDeactivated))
                .ToRabbitRoutingKey(ExchangeName, "property_deactivated", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
            opts.PublishMessage(typeof(PropertyArchived))
                .ToRabbitRoutingKey(ExchangeName, "property_archived", exchange => exchange.ExchangeType = ExchangeType.Topic)
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

    private static async Task<Guid> SeedPropertyAsync(IHost host, Guid tenantId, string code)
    {
        var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), code, $"Property {code}", 2, null, SomeAddress);
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);
        return created.Value.Id;
    }

    // ---- Tests: same-commit envelope / schema isolation ---------------------

    [Fact]
    public async Task Activating_a_property_persists_a_PropertyActivated_envelope_in_property_management_messaging_only()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var tenantId = Guid.NewGuid();
                var propertyId = await SeedPropertyAsync(host, tenantId, "LE-1");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
                    host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                result.IsSuccess.Should().BeTrue();

                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyActivated")).Should().Be(1);
                (await CountEnvelopesAsync("identity_messaging", "IHostPro.Contexts.PropertyManagement.Contracts.PropertyActivated")).Should().Be(0);
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
    public async Task Deactivating_a_property_persists_a_PropertyDeactivated_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var tenantId = Guid.NewGuid();
                var propertyId = await SeedPropertyAsync(host, tenantId, "LE-2");
                await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
                    host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(
                    host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                result.IsSuccess.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyDeactivated")).Should().Be(1);
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
    public async Task Archiving_a_property_persists_a_PropertyArchived_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var tenantId = Guid.NewGuid();
                var propertyId = await SeedPropertyAsync(host, tenantId, "LE-3");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
                    host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                result.IsSuccess.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyArchived")).Should().Be(1);
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
    public async Task A_rejected_activation_never_publishes_an_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var tenantId = Guid.NewGuid();
                var propertyId = await SeedPropertyAsync(host, tenantId, "LE-4");
                await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
                    host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                await rabbitMqContainer.StopAsync();

                // Already Active — this second activation is rejected.
                var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
                    host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

                result.IsFailure.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyActivated")).Should().Be(0);
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
    public async Task RabbitMQ_unavailable_does_not_block_the_commit_and_the_envelope_is_delivered_after_recovery()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var tenantId = Guid.NewGuid();
            Guid propertyId;

            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                propertyId = await SeedPropertyAsync(host, tenantId, "LE-5");

                await rabbitMqContainer.StopAsync();

                var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
                    host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

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
            WHERE message_type = 'IHostPro.Contexts.PropertyManagement.Contracts.PropertyActivated'
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
