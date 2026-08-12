using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Fase 6, Checkpoint 6 homologação, item 12 of the user's approved
/// protocol — completes what <see cref="HousekeepingEventProjectionTests"/>'s
/// own outbox test (Checkpoint 3) explicitly deferred: not just "the
/// envelope survives a broker outage, staged and undelivered" but the full
/// outage-THEN-recovery cycle — broker comes back, recovery (a fresh host's
/// own startup-time outbox scan — the same path a real restarted Worker
/// process takes, and never a fixed sleep as the primary sync) drains the
/// pending envelope and it is actually delivered exactly
/// once onto the real broker.
/// </summary>
public sealed class HousekeepingOutboxOutageRecoveryTests : IClassFixture<HousekeepingOutboxOutageRecoveryTests.Fixture>
{
    private const string OutboxSchema = "housekeeping_messaging";
    private const string MainSchema = "platform_messaging";

    private readonly Fixture _fixture;

    public HousekeepingOutboxOutageRecoveryTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            await _postgresContainer.StartAsync();

            var adminConnectionString = _postgresContainer.GetConnectionString();
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

            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(MigratorConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(HousekeepingDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });
            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var grantCommand = connection.CreateCommand();
            grantCommand.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE ON SCHEMA {MainSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MainSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MainSchema} TO ihostpro_app;
                """;
            await grantCommand.ExecuteNonQueryAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();

        private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
        {
            var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
                .Options;
            return new HousekeepingDbContext(options, tenantContext);
        }
    }

    [Fact]
    public async Task CleaningCancelled_staged_during_a_real_broker_outage_is_drained_and_delivered_exactly_once_after_the_broker_returns()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();

        IHost? host = null;
        try
        {
            // Mirrors IHostPro.MigrationRunner's own single-provisioning-
            // authority pattern: Wolverine's own PublishMessage/ToRabbitRoutingKey
            // never auto-declares the exchange on the broker (AutoProvision is
            // deliberately never enabled anywhere in this platform) — an
            // external provisioning step must declare it first.
            await DeclareTestExchangeAsync(rabbitMqContainer);

            host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);

            // Durable, non-exclusive so it survives the broker restart below —
            // an exclusive queue would be destroyed the instant its owning
            // connection drops when the broker goes down.
            var probeQueue = $"test-outage-recovery-probe-{Guid.NewGuid():N}";
            await using (var probeConnection = await CreateProbeConnectionAsync(rabbitMqContainer))
            {
                await using var probeChannel = await probeConnection.CreateChannelAsync();
                await probeChannel.QueueDeclareAsync(probeQueue, durable: true, exclusive: false, autoDelete: false);
                await probeChannel.QueueBindAsync(probeQueue, "housekeeping-events-test", "cleaning_cancelled");
            }

            // ---- Broker outage ----
            await rabbitMqContainer.StopAsync();

            var tenantId = Guid.NewGuid();
            var reservationId = Guid.NewGuid();
            var cleaningId = await SeedCleaningAsync(tenantId, reservationId);

            // The business transaction (Cleaning -> Cancelled, audit, envelope
            // staged) runs entirely against Postgres — never touches the
            // broker — so it must succeed even with RabbitMQ down.
            await Dispatch(host, tenantId, new ReservationCancelled
            {
                TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
                CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
                ReservationId = reservationId, PropertyId = Guid.NewGuid(),
            });

            (await ReadCleaningStatusAsync(tenantId, cleaningId)).Should().Be(CleaningStatus.Cancelled, "the business transaction must commit despite the broker being down");
            var auditCount = await CountCleaningCancelledAuditEntriesAsync(tenantId, cleaningId);
            auditCount.Should().Be(1, "the audit entry must commit in the same transaction, independent of broker availability");
            var stagedEnvelopeCount = await CountOutgoingCleaningCancelledEnvelopesAsync(tenantId);
            stagedEnvelopeCount.Should().BeGreaterThan(0, "CleaningCancelled must be durably staged in the outbox even though it could not be delivered");

            // ---- Broker returns — same container, same persisted topology ----
            await rabbitMqContainer.StartAsync();

            // Restart the host (stop the old one whose RabbitMQ connection was
            // forcibly severed by the outage, build and start a fresh one) —
            // the same recovery path a real deployment takes (a crashed/
            // restarted Worker process), and the most reliable way to trigger
            // Wolverine's own startup-time outbox recovery scan rather than
            // waiting on its background retry backoff schedule, whose first
            // interval is not a documented/stable constant to hard-couple a
            // test's timeout to.
            await host.StopAsync();
            host.Dispose();
            host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);

            // No fixed sleep as the primary sync: poll the real probe queue.
            BasicGetResult? delivered = null;
            await using (var pollConnection = await CreateProbeConnectionAsync(rabbitMqContainer))
            {
                await using var pollChannel = await pollConnection.CreateChannelAsync();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    delivered = await pollChannel.BasicGetAsync(probeQueue, autoAck: false);
                    if (delivered is not null) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }

                delivered.Should().NotBeNull("recovery (a fresh host's own startup outbox scan) must drain and deliver the staged CleaningCancelled once the broker is reachable again");
                await pollChannel.BasicAckAsync(delivered!.DeliveryTag, multiple: false);

                var second = await pollChannel.BasicGetAsync(probeQueue, autoAck: true);
                second.Should().BeNull("recovery must never re-deliver the same envelope a second time");

                await pollChannel.QueueDeleteAsync(probeQueue);
            }
        }
        finally
        {
            if (host is not null)
            {
                await host.StopAsync();
                host.Dispose();
            }
            await rabbitMqContainer.DisposeAsync();
        }
    }

    // ---- Service graph (real composition root, real broker) ----------------

    private async Task<IHost> BuildHostWithRabbitMqAsync(RabbitMqContainer rabbitMqContainer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Housekeeping"] = _fixture.AppConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddHousekeepingModule(configuration);

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });

            opts.PersistMessagesWithPostgresql(_fixture.AppConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, OutboxSchema, typeof(HousekeepingDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            opts.PublishMessage<CleaningCancelled>()
                .ToRabbitRoutingKey("housekeeping-events-test", "cleaning_cancelled", exchange => exchange.ExchangeType = Wolverine.RabbitMQ.ExchangeType.Topic)
                .UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task DeclareTestExchangeAsync(RabbitMqContainer rabbitMqContainer)
    {
        var declareHostBuilder = Host.CreateApplicationBuilder();
        declareHostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            }).DeclareExchange("housekeeping-events-test", exchange => exchange.ExchangeType = Wolverine.RabbitMQ.ExchangeType.Topic);
        });

        using var declareHost = declareHostBuilder.Build();
        await declareHost.SetupResources();
    }

    private static async Task<IConnection> CreateProbeConnectionAsync(RabbitMqContainer rabbitMqContainer)
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = rabbitMqContainer.Hostname,
            Port = rabbitMqContainer.GetMappedPublicPort(5672),
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = "/",
        };
        return await connectionFactory.CreateConnectionAsync();
    }

    private static async Task Dispatch<TEvent>(IHost host, Guid tenantId, TEvent @event)
        where TEvent : IntegrationEvent
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var handler = sp.GetRequiredService<IIntegrationEventHandler<TEvent>>();
        await handler.HandleAsync(@event, CancellationToken.None);
    }

    // ---- Seeding / reading (direct DbContext, migrator role) ----

    private async Task<Guid> SeedCleaningAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), reservationId, Guid.NewGuid(), now);

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return cleaning.Id;
    }

    private async Task<CleaningStatus> ReadCleaningStatusAsync(Guid tenantId, Guid cleaningId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var cleaning = await dbContext.Cleanings.FirstAsync(c => c.Id == cleaningId);
        return cleaning.Status;
    }

    private async Task<long> CountCleaningCancelledAuditEntriesAsync(Guid tenantId, Guid cleaningId)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM housekeeping.cleaning_audit_log WHERE action_code = 'cleaning_cancelled_by_reservation_cancellation' AND aggregate_id = @id";
        command.Parameters.AddWithValue("id", cleaningId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private async Task<long> CountOutgoingCleaningCancelledEnvelopesAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type = @messageType
              AND position(convert_to(@tenantId, 'UTF8') in body) > 0
            """;
        command.Parameters.AddWithValue("messageType", "IHostPro.Contexts.Housekeeping.Contracts.CleaningCancelled");
        command.Parameters.AddWithValue("tenantId", tenantId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
    }
}
