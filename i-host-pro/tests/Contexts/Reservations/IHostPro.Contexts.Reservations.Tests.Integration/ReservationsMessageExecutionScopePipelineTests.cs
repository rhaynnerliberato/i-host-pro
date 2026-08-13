using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Messaging;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// Real-pipeline regression coverage for <see cref="IReservationsMessageExecutionScope"/>
/// (Fase 7, Checkpoint 1 CLOSURE — ADR-016). Dispatches through Wolverine's
/// REAL generated handler chains (<c>opts.Discovery</c> + <c>IMessageBus.InvokeAsync</c>,
/// no RabbitMQ transport needed — Wolverine falls back to its default
/// local/in-process queue) rather than calling
/// <c>CleaningScheduleProjectionSynchronizer</c> directly, so it exercises
/// the exact DI-resolution path Wolverine's codegen uses in production —
/// the only path where the original defect (real SQL evidence: <c>WHERE
/// FALSE</c> on <c>CleaningAssigned</c>'s projection lookup, because
/// <c>ReservationsDbContext</c>'s Global Query Filter closed over a
/// different, never-resolved <c>ITenantContext</c> instance than the one
/// <c>TenantResolutionMiddleware</c> populated) was ever observable.
///
/// This file replaces the temporary root-cause-investigation harness
/// (<c>ExperimentRealPipelineReproTests.cs</c>) that first reproduced and
/// then, after the boundary was added, disproved the defect — kept here as
/// a permanent regression test since going through the real generated chain
/// (rather than direct DI resolution, as
/// <c>CleaningScheduleProjectionSynchronizerTests</c> does) is exactly what
/// makes this coverage non-redundant.
/// </summary>
public class ReservationsMessageExecutionScopePipelineTests : IClassFixture<ReservationsMessageExecutionScopePipelineTests.Fixture>
{
    private const string MainSchema = "platform_messaging";
    private const string ReservationsOutboxSchema = "reservations_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ReservationsMessageExecutionScopePipelineTests(Fixture fixture)
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
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
                await reservationsDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(ReservationsOutboxSchema, typeof(ReservationsDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionMainStoreAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(MigratorConnectionString, MainSchema);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var mainHost = hostBuilder.Build();
            await mainHost.SetupResources();

            await GrantSchemaAsync(MainSchema);
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextMarkerType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextMarkerType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await GrantSchemaAsync(schema);
        }

        private async Task GrantSchemaAsync(string schema)
        {
            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {schema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }

        private static ReservationsDbContext CreateReservationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
                .Options;
            return new ReservationsDbContext(options, new TenantContext());
        }
    }

    // ---- Host: mirrors IHostPro.Worker's own Wolverine config for the Reservations
    //      schedule-projection consumer, minus RabbitMQ (IMessageBus.InvokeAsync
    //      dispatches through the same real generated handler chains via Wolverine's
    //      default local/in-process queue when no transport is registered for a
    //      given message type). ----

    private IHost BuildHost()
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddDbContext<ReservationsDbContext>(options =>
            options.UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")));
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddReservationsScheduleProjectionConsumer();

        hostBuilder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ReservationsOutboxSchema, typeof(ReservationsDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            opts.Policies.AddMiddleware(
                typeof(TenantResolutionMiddleware),
                chain => typeof(IntegrationEvent).IsAssignableFrom(chain.MessageType));

            opts.CodeGeneration.AlwaysUseServiceLocationFor<IReservationsMessageExecutionScope>();

            opts.Discovery.IncludeAssembly(typeof(CleaningCreatedHandler).Assembly);
        });

        return hostBuilder.Build();
    }

    [Fact]
    public async Task CleaningAssigned_updates_the_projection_through_the_real_Wolverine_pipeline()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();
        var scheduledAtUtc = DateTimeOffset.UtcNow.AddHours(3);

        await DispatchAsync(host, tenantId, new CleaningCreated
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = propertyId, Status = "Pending", ScheduledAtUtc = scheduledAtUtc,
        });

        var afterCreated = await ReadEntryAsync(tenantId, cleaningId);
        afterCreated.Should().NotBeNull("CleaningCreated must reach the projection via the real pipeline");
        afterCreated!.Value.Status.Should().Be("Pending");

        await DispatchAsync(host, tenantId, new CleaningAssigned
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = housekeeperUserId,
        });

        var afterAssigned = await ReadEntryAsync(tenantId, cleaningId);
        afterAssigned.Should().NotBeNull("the tenant execution scope must resolve the same ITenantContext the EF Global Query Filter observes (ADR-016)");
        afterAssigned!.Value.Status.Should().Be("Assigned");
        afterAssigned.Value.AssignedHousekeeperUserId.Should().Be(housekeeperUserId);
        afterAssigned.Value.PropertyId.Should().Be(propertyId, "CleaningAssigned must not disturb fields it does not own");
    }

    [Fact]
    public async Task CleaningAssigned_for_one_tenant_never_updates_another_tenants_row()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await DispatchAsync(host, tenantA, new CleaningCreated
        {
            TenantId = tenantA, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = propertyId, Status = "Pending",
        });
        await DispatchAsync(host, tenantB, new CleaningCreated
        {
            TenantId = tenantB, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = propertyId, Status = "Pending",
        });

        await DispatchAsync(host, tenantA, new CleaningAssigned
        {
            TenantId = tenantA, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = Guid.NewGuid(),
        });

        var tenantARow = await ReadEntryAsync(tenantA, cleaningId);
        var tenantBRow = await ReadEntryAsync(tenantB, cleaningId);

        tenantARow!.Value.Status.Should().Be("Assigned");
        tenantBRow!.Value.Status.Should().Be("Pending", "cross-tenant isolation: assigning tenant A's cleaning must never affect tenant B's row with the same CleaningId");
        tenantBRow.Value.AssignedHousekeeperUserId.Should().BeNull();
    }

    /// <summary>
    /// Consequence of the same root cause (ADR-016): before
    /// <c>CleaningCreatedHandler</c> was migrated to the execution scope,
    /// <c>CleaningScheduleProjectionSynchronizer.HandleAsync(CleaningCreated, ...)</c>'s
    /// own idempotency guard (<c>AnyAsync</c>, an EF query) always evaluated
    /// <c>SELECT FALSE</c> — a real redelivery of the same
    /// <c>CleaningCreated</c> would have attempted a second <c>INSERT</c>
    /// against the table's composite primary key
    /// (<c>tenant_id, cleaning_id</c>) and thrown <c>DbUpdateException</c>,
    /// not silently no-op'd as the synchronizer's own doc comment promises.
    /// </summary>
    [Fact]
    public async Task Redelivered_CleaningCreated_is_a_harmless_no_op()
    {
        using var host = BuildHost();
        await host.StartAsync();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var cleaningCreated = new CleaningCreated
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = propertyId, Status = "Pending",
        };

        await DispatchAsync(host, tenantId, cleaningCreated);

        var redeliveryAct = async () => await DispatchAsync(host, tenantId, cleaningCreated);
        await redeliveryAct.Should().NotThrowAsync("a redelivered CleaningCreated must be a harmless no-op, per the synchronizer's own idempotency contract");

        var row = await ReadEntryAsync(tenantId, cleaningId);
        row!.Value.Status.Should().Be("Pending");
        row.Value.PropertyId.Should().Be(propertyId);
    }

    private static async Task DispatchAsync(IHost host, Guid tenantId, IntegrationEvent message)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(message);
    }

    private async Task<(Guid PropertyId, Guid? AssignedHousekeeperUserId, string Status)?> ReadEntryAsync(Guid tenantId, Guid cleaningId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT property_id, assigned_housekeeper_user_id, status
            FROM reservations.cleaning_schedule_projection
            WHERE tenant_id = @tenantId AND cleaning_id = @cleaningId
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("cleaningId", cleaningId);

        await using var reader = await command.ExecuteReaderAsync();
        (Guid, Guid?, string)? row = null;
        if (await reader.ReadAsync())
        {
            row = (
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return row;
    }
}
