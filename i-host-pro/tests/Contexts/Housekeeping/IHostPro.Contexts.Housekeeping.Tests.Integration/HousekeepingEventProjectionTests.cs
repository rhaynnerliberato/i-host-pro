using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Exercises the Checkpoint 3 event-projection plumbing against a real
/// PostgreSQL instance and the real composition root (<c>AddHousekeepingModule</c>
/// + a real Wolverine outbox) — never a fake executor: <c>PropertyProjectionSynchronizer</c>/
/// <c>ReservationProjectionAndCancellationReaction</c> are resolved from DI
/// exactly as <c>IHostPro.Worker</c> resolves them, and every event is
/// dispatched through <c>IIntegrationEventHandler&lt;T&gt;.HandleAsync</c>
/// directly — the same call the six thin Wolverine adapters in
/// <c>Housekeeping.Infrastructure.Messaging</c> make, without needing a live
/// RabbitMQ broker (deferred to Checkpoint 6, mirroring
/// <c>PolicyCacheAndOutboxTests</c>'s own documented reasoning).
///
/// "Idempotência"/"outage/recovery" (Checkpoint 3 minimum test list) are
/// covered by dispatching the SAME event twice — the realistic shape of an
/// at-least-once redelivery after a consumer crash between commit and ack.
/// </summary>
public class HousekeepingEventProjectionTests : IClassFixture<HousekeepingEventProjectionTests.Fixture>
{
    private const string OutboxSchema = "housekeeping_messaging";
    private const string MainSchema = "platform_messaging";

    private readonly Fixture _fixture;

    public HousekeepingEventProjectionTests(Fixture fixture) => _fixture = fixture;

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

            await ProvisionOutboxAndMainStoreAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionOutboxAndMainStoreAsMigratorAsync()
        {
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
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE ON SCHEMA {MainSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MainSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MainSchema} TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Property projection: PropertyCreated/Activated/Deactivated/Archived ----

    [Fact]
    public async Task PropertyCreated_projects_the_property_as_inactive()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await Dispatch(host, tenantId, new PropertyCreated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId, Status = "draft",
        });

        var entry = await ReadProjectionAsync(host, tenantId, propertyId);
        entry.Should().NotBeNull();
        entry!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task PropertyActivated_then_PropertyDeactivated_flips_the_projection_accordingly()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await Dispatch(host, tenantId, new PropertyCreated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId, Status = "draft",
        });
        await Dispatch(host, tenantId, new PropertyActivated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        });

        (await ReadProjectionAsync(host, tenantId, propertyId))!.IsActive.Should().BeTrue();

        await Dispatch(host, tenantId, new PropertyDeactivated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        });

        (await ReadProjectionAsync(host, tenantId, propertyId))!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task PropertyArchived_marks_the_projection_inactive()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await Dispatch(host, tenantId, new PropertyCreated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId, Status = "draft",
        });
        await Dispatch(host, tenantId, new PropertyActivated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        });
        await Dispatch(host, tenantId, new PropertyArchived
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        });

        (await ReadProjectionAsync(host, tenantId, propertyId))!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Redelivering_the_same_PropertyActivated_event_is_idempotent_no_duplicate_row()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var @event = new PropertyActivated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        };

        await Dispatch(host, tenantId, @event);
        await Dispatch(host, tenantId, @event); // simulated redelivery after an "outage"

        var count = await CountProjectionRowsAsync(host, tenantId, propertyId);
        count.Should().Be(1);
        (await ReadProjectionAsync(host, tenantId, propertyId))!.IsActive.Should().BeTrue();
    }

    // ---- Reservation projection: ReservationCreated ----

    [Fact]
    public async Task ReservationCreated_projects_the_reservation_reference()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await Dispatch(host, tenantId, new ReservationCreated
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(), Status = "confirmed",
        });

        var exists = await ReservationProjectionExistsAsync(host, tenantId, reservationId);
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task Redelivering_the_same_ReservationCreated_event_is_idempotent_no_duplicate_row()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var @event = new ReservationCreated
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(), Status = "confirmed",
        };

        await Dispatch(host, tenantId, @event);
        await Dispatch(host, tenantId, @event);

        var count = await CountReservationProjectionRowsAsync(host, tenantId, reservationId);
        count.Should().Be(1);
    }

    // ---- ReservationCancelled: automatic Cleaning cancellation ----

    [Fact]
    public async Task ReservationCancelled_auto_cancels_a_linked_Pending_cleaning_and_publishes_CleaningCancelled_via_outbox()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var tenantId = Guid.NewGuid();
                var reservationId = Guid.NewGuid();
                var cleaningId = await SeedCleaningAsync(tenantId, reservationId, CleaningStatus.Pending);

                await Dispatch(host, tenantId, new ReservationCancelled
                {
                    TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
                    CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
                    ReservationId = reservationId, PropertyId = Guid.NewGuid(),
                });

                var status = await ReadCleaningStatusAsync(tenantId, cleaningId);
                status.Should().Be(CleaningStatus.Cancelled);

                var outboxCount = await CountOutgoingCleaningCancelledEnvelopesAsync(tenantId);
                outboxCount.Should().BeGreaterThan(0, "the auto-cancellation must publish CleaningCancelled through the durable outbox");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReservationCancelled_auto_cancels_a_linked_Assigned_cleaning()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var cleaningId = await SeedCleaningAsync(tenantId, reservationId, CleaningStatus.Assigned);

        await Dispatch(host, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(),
        });

        (await ReadCleaningStatusAsync(tenantId, cleaningId)).Should().Be(CleaningStatus.Cancelled);
    }

    [Fact]
    public async Task ReservationCancelled_never_touches_a_linked_Completed_cleaning()
    {
        using var host = await BuildHostAsync();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var cleaningId = await SeedCleaningAsync(tenantId, reservationId, CleaningStatus.Completed);

        await Dispatch(host, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(),
        });

        (await ReadCleaningStatusAsync(tenantId, cleaningId)).Should().Be(CleaningStatus.Completed);
    }

    /// <summary>
    /// Fase 6, Checkpoint 6 homologação — business-level idempotency proof,
    /// deliberately independent of transport (the user's approved protocol,
    /// item 8): each <see cref="Dispatch{TEvent}"/> below opens its own fresh
    /// <c>host.Services.CreateScope()</c>, so the second call is a genuinely
    /// new child scope resolving a brand-new
    /// <c>ReservationProjectionAndCancellationReaction</c> instance — never a
    /// second invocation on an already-used instance — with zero involvement
    /// from Wolverine's transport at all (this call path is exactly what the
    /// six Wolverine adapters delegate to via
    /// <c>IHousekeepingMessageExecutionScope</c>, ADR-015). This proves the
    /// reaction's own query-before-cancel guard is what prevents duplicate
    /// effects — the ONLY protection that exists, confirmed by a real
    /// Wolverine/RabbitMQ redelivery of the identical wire envelope in
    /// <c>ReservationCancelledRedeliveryTests</c> (Host tests) combined with
    /// the direct runtime inspection in
    /// <c>HousekeepingListenerDurabilityModeTests</c>: this queue runs in
    /// <c>EndpointMode.Inline</c>, never <c>Durable</c> (ADR-015, §10.8 of
    /// the Fase 6 homologation doc) — no durable-inbox row is ever created
    /// for this queue, so there is no row to "clear" and nothing at the
    /// transport level rejects a redelivery. Domain-level idempotency,
    /// proven here, is the only protection.
    /// </summary>
    [Fact]
    public async Task Redelivering_the_same_ReservationCancelled_event_is_idempotent_no_duplicate_cancellation_events()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var tenantId = Guid.NewGuid();
                var reservationId = Guid.NewGuid();
                var cleaningId = await SeedCleaningAsync(tenantId, reservationId, CleaningStatus.Pending);
                var @event = new ReservationCancelled
                {
                    TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
                    CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
                    ReservationId = reservationId, PropertyId = Guid.NewGuid(),
                };

                await Dispatch(host, tenantId, @event);
                var outboxCountAfterFirst = await CountOutgoingCleaningCancelledEnvelopesAsync(tenantId);
                outboxCountAfterFirst.Should().BeGreaterThan(0);
                var auditCountAfterFirst = await CountCleaningCancelledAuditEntriesAsync(tenantId, cleaningId);
                auditCountAfterFirst.Should().Be(1, "the first processing must produce exactly one audit entry");

                // Simulated redelivery, in a genuinely new child scope (see
                // this test's own doc comment) — never relying on Wolverine's
                // inbox to have already rejected it: the Cleaning is already
                // Cancelled, so the query behind the reaction matches zero
                // rows — no exception, no duplicate audit, no duplicate
                // CleaningCancelled.
                var act = async () => await Dispatch(host, tenantId, @event);
                await act.Should().NotThrowAsync();

                (await ReadCleaningStatusAsync(tenantId, cleaningId)).Should().Be(CleaningStatus.Cancelled);
                var outboxCountAfterSecond = await CountOutgoingCleaningCancelledEnvelopesAsync(tenantId);
                outboxCountAfterSecond.Should().Be(outboxCountAfterFirst, "a redelivered ReservationCancelled must never publish a duplicate CleaningCancelled");
                var auditCountAfterSecond = await CountCleaningCancelledAuditEntriesAsync(tenantId, cleaningId);
                auditCountAfterSecond.Should().Be(1, "a redelivered ReservationCancelled, processed in a fresh child scope, must never produce a second audit entry");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    // ---- Service graph (real composition root) -----------------------------

    private async Task<IHost> BuildHostAsync()
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
            opts.PersistMessagesWithPostgresql(_fixture.AppConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, OutboxSchema, typeof(HousekeepingDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A short-lived host, separate from <see cref="BuildHostAsync"/>, built
    /// fresh per outbox test with a real (deliberately stopped-before-asserting)
    /// RabbitMQ — mirrors <c>PolicyCacheAndOutboxTests.BuildHostWithRabbitMqAsync</c>
    /// exactly: the container is started so Wolverine can declare the real
    /// topology at startup, then stopped before the action under test runs,
    /// so <c>CleaningCancelled</c> is durably staged in the Postgresql-backed
    /// outbox but never actually delivered — proving persistence without
    /// needing a live end-to-end RabbitMQ round trip (deferred to Checkpoint 6).
    /// </summary>
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
                .ToRabbitRoutingKey("housekeeping-events-test", "cleaning_cancelled", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
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

    private static async Task Dispatch<TEvent>(IHost host, Guid tenantId, TEvent @event)
        where TEvent : IntegrationEvent
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var handler = sp.GetRequiredService<IIntegrationEventHandler<TEvent>>();
        await handler.HandleAsync(@event, CancellationToken.None);
    }

    // ---- Seeding / reading (direct DbContext, migrator role — bypasses RLS for setup/assertions) ----

    private async Task<Guid> SeedCleaningAsync(Guid tenantId, Guid reservationId, CleaningStatus targetStatus)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), reservationId, Guid.NewGuid(), now);

        switch (targetStatus)
        {
            case CleaningStatus.Pending:
                break;
            case CleaningStatus.Assigned:
                cleaning.Assign(Guid.NewGuid(), now);
                break;
            case CleaningStatus.Completed:
                cleaning.Assign(Guid.NewGuid(), now);
                cleaning.Start(now);
                cleaning.StartInspection(now);
                cleaning.Complete(now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(targetStatus), targetStatus, "Unsupported seeding target status.");
        }

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

    private async Task<PropertyProjectionRow?> ReadProjectionAsync(IHost host, Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.PropertyProjection
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId);

        return entry is null ? null : new PropertyProjectionRow(entry.IsActive);
    }

    private sealed record PropertyProjectionRow(bool IsActive);

    private async Task<int> CountProjectionRowsAsync(IHost host, Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.PropertyProjection.CountAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId);
    }

    private async Task<bool> ReservationProjectionExistsAsync(IHost host, Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.ReservationProjection.AnyAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId);
    }

    private async Task<int> CountReservationProjectionRowsAsync(IHost host, Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.ReservationProjection.CountAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId);
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
