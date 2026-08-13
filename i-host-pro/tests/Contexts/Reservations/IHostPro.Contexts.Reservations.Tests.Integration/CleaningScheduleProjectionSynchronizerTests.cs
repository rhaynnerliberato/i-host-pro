using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Projections;
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
/// Fase 7, Incremento 1 (Agenda Foundation), Checkpoint 1 CLOSURE: drives
/// <see cref="CleaningScheduleProjectionSynchronizer"/> directly (bypassing
/// Wolverine's own RabbitMQ dispatch, but never bypassing the real
/// tenant-aware/RLS-protected PostgreSQL write path) to prove every
/// documented <c>Cleaning.Status</c> transition (Documento 07 §29.7),
/// redelivery idempotency, out-of-order safety, and RLS fail-closed
/// isolation — cheaper than a full RabbitMQ/Worker round trip for the 8
/// transitions whose real broker delivery is not individually re-proven
/// here. Real broker delivery itself is proven once, end-to-end, by
/// <c>CleaningCreatedScheduleProjectionWorkerRoundTripTests</c> and this
/// checkpoint's own <c>CleaningNeedsHelpScheduleProjectionWorkerRoundTripTests</c>
/// gate — this file's own subject is the synchronizer's business logic
/// against a real Postgres database, not transport.
/// </summary>
public class CleaningScheduleProjectionSynchronizerTests : IClassFixture<CleaningScheduleProjectionSynchronizerTests.Fixture>
{
    private const string MainSchema = "platform_messaging";
    private const string ReservationsOutboxSchema = "reservations_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public CleaningScheduleProjectionSynchronizerTests(Fixture fixture)
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

    // ---- Host / DI --------------------------------------------------------

    private IHost BuildHost()
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddDbContext<ReservationsDbContext>(options =>
            options.UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")));
        hostBuilder.Services.AddScoped<TenantContext>();
        hostBuilder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        hostBuilder.Services.AddReservationsScheduleProjectionConsumer();

        hostBuilder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ReservationsOutboxSchema, typeof(ReservationsDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    /// <summary>
    /// Resolves the synchronizer from a fresh DI scope with the given tenant
    /// already set on <see cref="ITenantContext"/> — mirrors how a real
    /// consumed message's <c>TenantResolutionMiddleware</c> would populate it
    /// in <c>IHostPro.Worker</c>, one scope per message.
    /// </summary>
    private static async Task InvokeAsync(IHost host, Guid tenantContextTenantId, Func<CleaningScheduleProjectionSynchronizer, Task> action)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantContextTenantId);
        var synchronizer = scope.ServiceProvider.GetRequiredService<CleaningScheduleProjectionSynchronizer>();
        await action(synchronizer);
    }

    private async Task<CleaningScheduleProjectionEntry?> ReadEntryAsync(Guid tenantId, Guid cleaningId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.CleaningScheduleProjection.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId);

        await transaction.CommitAsync();
        return entry;
    }

    private async Task<int> CountEntriesAsync(Guid tenantId, Guid cleaningId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var count = await dbContext.CleaningScheduleProjection
            .CountAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId);

        await transaction.CommitAsync();
        return count;
    }

    private static async Task SetTenantAsync(ReservationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private ReservationsDbContext CreateReservationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new ReservationsDbContext(options, tenantContext);
    }

    // ---- Event builders -----------------------------------------------------

    private static CleaningCreated NewCreated(Guid tenantId, Guid cleaningId, Guid propertyId, DateTimeOffset? scheduledAtUtc = null) => new()
    {
        TenantId = tenantId,
        AggregateId = cleaningId,
        AggregateType = "Cleaning",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        CleaningId = cleaningId,
        PropertyId = propertyId,
        Status = "Pending",
        ScheduledAtUtc = scheduledAtUtc,
    };

    // ---- CleaningCreated ----------------------------------------------------

    [Fact]
    public async Task CleaningCreated_inserts_a_new_row_with_Pending_status_and_no_housekeeper()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId, scheduledAtUtc)));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry.Should().NotBeNull();
        entry!.PropertyId.Should().Be(propertyId);
        entry.Status.Should().Be("Pending");
        entry.ScheduledAtUtc.Should().Be(scheduledAtUtc);
        entry.AssignedHousekeeperUserId.Should().BeNull();
    }

    [Fact]
    public async Task CleaningCreated_is_idempotent_on_redelivery_and_never_creates_a_duplicate_row()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var created = NewCreated(tenantId, cleaningId, propertyId);

        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));

        (await CountEntriesAsync(tenantId, cleaningId)).Should().Be(1);
    }

    // ---- Status matrix (Documento 07 §29.7) ----------------------------------

    [Fact]
    public async Task CleaningAssigned_updates_status_to_Assigned_and_sets_the_housekeeper()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningAssigned
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = housekeeperUserId,
        }));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Assigned");
        entry.AssignedHousekeeperUserId.Should().Be(housekeeperUserId);
    }

    [Fact]
    public async Task CleaningInTransit_updates_status_to_InTransit()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningInTransit
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("InTransit");
    }

    [Fact]
    public async Task CleaningStarted_updates_status_to_Started()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningStarted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("Started");
    }

    [Fact]
    public async Task CleaningInspectionStarted_updates_status_to_InInspection()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningInspectionStarted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("InInspection");
    }

    [Fact]
    public async Task CleaningCompleted_updates_status_to_Completed()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningCompleted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = Guid.NewGuid(),
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task CleaningInterrupted_updates_status_to_Interrupted()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningInterrupted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("Interrupted");
    }

    [Fact]
    public async Task CleaningNeedsHelp_updates_status_to_WaitingHelp()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningNeedsHelp
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("WaitingHelp");
    }

    [Fact]
    public async Task CleaningNeedsMaterial_updates_status_to_WaitingMaterials()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningNeedsMaterial
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("WaitingMaterials");
    }

    [Fact]
    public async Task CleaningCancelled_updates_status_to_Cancelled()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid())));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningCancelled
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));

        (await ReadEntryAsync(tenantId, cleaningId))!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task A_realistic_sequential_lifecycle_leaves_the_projection_correctly_in_its_final_state()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId)));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningAssigned
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = housekeeperUserId,
        }));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningInTransit
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = housekeeperUserId.ToString(),
            CleaningId = cleaningId,
        }));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningStarted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = housekeeperUserId.ToString(),
            CleaningId = cleaningId,
        }));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningInspectionStarted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId,
        }));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningCompleted
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, PropertyId = Guid.NewGuid(),
        }));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Completed");
        entry.AssignedHousekeeperUserId.Should().Be(housekeeperUserId, "the housekeeper set by CleaningAssigned must survive every later transition");
        entry.PropertyId.Should().Be(propertyId);
        (await CountEntriesAsync(tenantId, cleaningId)).Should().Be(1, "the whole lifecycle must update a single row, never insert a second one");
    }

    // ---- Redelivery / out-of-order / RLS -------------------------------------

    [Fact]
    public async Task A_status_updating_event_is_idempotent_on_redelivery_and_never_creates_a_duplicate_row_or_corrupts_other_fields()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId)));

        var assigned = new CleaningAssigned
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = housekeeperUserId,
        };

        await InvokeAsync(host, tenantId, s => s.HandleAsync(assigned));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(assigned));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Assigned");
        entry.AssignedHousekeeperUserId.Should().Be(housekeeperUserId);
        entry.PropertyId.Should().Be(propertyId);
        (await CountEntriesAsync(tenantId, cleaningId)).Should().Be(1);
    }

    [Fact]
    public async Task A_status_updating_event_for_a_Cleaning_whose_CleaningCreated_was_never_projected_is_silently_ignored()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();

        await InvokeAsync(host, tenantId, s => s.HandleAsync(new CleaningAssigned
        {
            TenantId = tenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = Guid.NewGuid(),
        }));

        (await ReadEntryAsync(tenantId, cleaningId)).Should().BeNull(
            "out-of-order delivery before CleaningCreated must never fabricate a row from a status-updating event alone");
    }

    [Fact]
    public async Task A_status_updating_event_is_invisible_when_the_message_scope_tenant_does_not_match_the_rows_owning_tenant_RLS_fail_closed()
    {
        using var host = BuildHost();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        await InvokeAsync(host, ownerTenantId, s => s.HandleAsync(NewCreated(ownerTenantId, cleaningId, Guid.NewGuid())));

        // The event payload still claims the row's real TenantId, but the
        // DI-scoped ITenantContext (which drives the RLS session via
        // TenantAwareTransactionScope, exactly as a real
        // TenantResolutionMiddleware-populated scope would) is deliberately
        // set to a DIFFERENT tenant — simulating a misrouted/misresolved
        // scope. RLS must make the row invisible regardless of what the
        // event's own TenantId column says, so the write silently no-ops.
        await InvokeAsync(host, otherTenantId, s => s.HandleAsync(new CleaningAssigned
        {
            TenantId = ownerTenantId, AggregateId = cleaningId, AggregateType = "Cleaning",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            CleaningId = cleaningId, HousekeeperUserId = Guid.NewGuid(),
        }));

        var entry = await ReadEntryAsync(ownerTenantId, cleaningId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("Pending", "a mismatched tenant scope must never be able to update another tenant's row, even carrying the correct TenantId in the payload");
        entry.AssignedHousekeeperUserId.Should().BeNull();
    }
}
