using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
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
/// Validates Property's own durable outbox end-to-end against real
/// PostgreSQL and RabbitMQ (Testcontainers) — mirrors
/// <c>CondominiumIntegrationEventsTests</c> exactly: same-commit envelope
/// persistence, schema isolation from <c>identity_messaging</c>, broker
/// outage not blocking commit, and lossless delivery after recovery
/// (Checkpoint 3 plan, item 16). Dispatches through the REAL production
/// composition root via <see cref="ISender"/>.
/// </summary>
public class PropertyIntegrationEventsTests : IClassFixture<PropertyIntegrationEventsTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";
    private const string ExchangeName = "property-management-events-test";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PropertyIntegrationEventsTests(Fixture fixture)
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

        /// <summary>
        /// A second, unrelated outbox schema — exists purely so this class's
        /// "no envelope in identity_messaging" test has a real, provisioned
        /// schema to query against.
        /// </summary>
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

            // Faster ownership hand-off for the ancillary store's durability
            // agent — mirrors CondominiumIntegrationEventsTests exactly.
            opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(1);
            opts.Durability.FirstHealthCheckExecution = TimeSpan.FromSeconds(1);
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(5);

            opts.PublishMessage(typeof(PropertyCreated))
                .ToRabbitRoutingKey(ExchangeName, "property_created", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
            opts.PublishMessage(typeof(PropertyUpdated))
                .ToRabbitRoutingKey(ExchangeName, "property_updated", exchange => exchange.ExchangeType = ExchangeType.Topic)
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

    // ---- Tests: same-commit envelope / schema isolation ---------------------

    /// <summary>
    /// RabbitMQ is stopped BEFORE the command runs — same technique as
    /// <c>CondominiumIntegrationEventsTests</c> (see its own doc comment for
    /// the full rationale): Wolverine's first delivery attempt after commit
    /// is synchronous and near-immediate when the broker IS reachable.
    /// </summary>
    [Fact]
    public async Task Creating_a_property_persists_a_PropertyCreated_envelope_in_property_management_messaging_only()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var tenantId = Guid.NewGuid();
                var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-1", "Studio 1", 2, null, SomeAddress);
                var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);

                result.IsSuccess.Should().BeTrue();

                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyCreated")).Should().Be(1);
                (await CountEnvelopesAsync("identity_messaging", "IHostPro.Contexts.PropertyManagement.Contracts.PropertyCreated")).Should().Be(0);
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
    public async Task Updating_a_property_persists_a_PropertyUpdated_envelope_with_ChangedFields()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                var tenantId = Guid.NewGuid();
                var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
                    host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-2", "Original", 2, null, SomeAddress), tenantId);

                await rabbitMqContainer.StopAsync();

                var update = new UpdatePropertyCommand(
                    tenantId, Guid.NewGuid(), created.Value.Id,
                    Optional<string>.Unset, Optional<string>.Of("Updated"), Optional<int>.Unset,
                    Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
                var result = await ExecuteAsync<UpdatePropertyCommand, PropertyResult>(host, update, tenantId);

                result.IsSuccess.Should().BeTrue();
                (await CountEnvelopesAsync(OutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.PropertyUpdated")).Should().Be(1);
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

            var host = await BuildHostAsync(rabbitMqContainer); // built while RabbitMQ is still up
            try
            {
                await rabbitMqContainer.StopAsync();

                var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-3", "Studio 3", 2, null, SomeAddress);
                var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);

                result.IsSuccess.Should().BeTrue(); // commit succeeds despite the broker being down
                propertyId = result.Value.Id;

                await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
                await using var transaction = await dbContext.Database.BeginTransactionAsync();
                await SetPostgresTenantAsync(dbContext, tenantId);
                (await dbContext.Properties.CountAsync(p => p.Id == propertyId)).Should().Be(1);

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
            WHERE message_type = 'IHostPro.Contexts.PropertyManagement.Contracts.PropertyCreated'
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

    private static async Task SetPostgresTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
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
