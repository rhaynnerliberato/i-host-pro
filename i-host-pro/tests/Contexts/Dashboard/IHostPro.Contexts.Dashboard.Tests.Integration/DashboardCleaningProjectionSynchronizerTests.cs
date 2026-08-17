using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Dashboard.Tests.Integration;

/// <summary>
/// Fase 7, Incremento 2 (Checkpoint 2, mandate §6/§7/§8/§20) — closes a
/// coverage gap surfaced during the Checkpoint 2 audit:
/// <see cref="DashboardCleaningProjectionSynchronizer"/> (the CleaningCreated
/// fan-out target this checkpoint's mandate specifically asks for evidence
/// of) had zero dedicated test coverage since Checkpoint 1 — only
/// <c>DashboardReservationProjectionSynchronizerTests</c> existed. Drives the
/// synchronizer directly (bypassing RabbitMQ, never bypassing the real
/// tenant-aware/RLS-protected PostgreSQL write path), mirroring
/// <c>DashboardReservationProjectionSynchronizerTests</c>'s own structure —
/// proportionally scoped to creation + one status transition + cancellation
/// (CancelledAtUtc/CompletedAtUtc) + idempotency, not an exhaustive
/// ten-event/out-of-order-guard proof (that mechanism is already proven
/// generically by the Reservation tests — every synchronizer shares the
/// exact same <c>_executor.ExecuteAsync</c> + <c>eventAtUtc &gt;= entry.LastEventAtUtc</c>
/// guard).
/// </summary>
public class DashboardCleaningProjectionSynchronizerTests : IClassFixture<DashboardCleaningProjectionSynchronizerTests.Fixture>
{
    private const string MainSchema = "platform_messaging";
    private const string DashboardOutboxSchema = "dashboard_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public DashboardCleaningProjectionSynchronizerTests(Fixture fixture)
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

            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
                await identityDbContext.Database.MigrateAsync();

            await using (var dashboardDbContext = CreateDashboardDbContext(MigratorConnectionString))
                await dashboardDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(DashboardOutboxSchema, typeof(DashboardDbContext));
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

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static DashboardDbContext CreateDashboardDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<DashboardDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
                .Options;
            return new DashboardDbContext(options, new TenantContext());
        }
    }

    // ---- Host / DI --------------------------------------------------------

    private IHost BuildHost()
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dashboard"] = _appConnectionString })
            .Build();

        hostBuilder.Services.AddScoped<TenantContext>();
        hostBuilder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        hostBuilder.Services.AddDashboardModule(configuration);
        hostBuilder.Services.AddDashboardProjectionConsumer();

        hostBuilder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, DashboardOutboxSchema, typeof(DashboardDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    private static async Task InvokeAsync(IHost host, Guid tenantContextTenantId, Func<DashboardCleaningProjectionSynchronizer, Task> action)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantContextTenantId);
        var synchronizer = scope.ServiceProvider.GetRequiredService<DashboardCleaningProjectionSynchronizer>();
        await action(synchronizer);
    }

    private async Task<DashboardCleaningProjectionEntry?> ReadEntryAsync(Guid tenantId, Guid cleaningId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.CleaningProjection.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId);

        await transaction.CommitAsync();
        return entry;
    }

    private async Task<int> CountEntriesAsync(Guid tenantId, Guid cleaningId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var count = await dbContext.CleaningProjection.CountAsync(c => c.TenantId == tenantId && c.CleaningId == cleaningId);

        await transaction.CommitAsync();
        return count;
    }

    private static async Task SetTenantAsync(DashboardDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private DashboardDbContext CreateDashboardDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
            .Options;
        return new DashboardDbContext(options, tenantContext);
    }

    // ---- Event builders -----------------------------------------------------

    private static CleaningCreated NewCreated(
        Guid tenantId, Guid cleaningId, Guid propertyId, DateTimeOffset timestamp, DateTimeOffset? scheduledAtUtc = null) => new()
    {
        TenantId = tenantId,
        AggregateId = cleaningId,
        AggregateType = "Cleaning",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        CleaningId = cleaningId,
        PropertyId = propertyId,
        Status = "Pending",
        ScheduledAtUtc = scheduledAtUtc,
    };

    private static CleaningStarted NewStarted(Guid tenantId, Guid cleaningId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = cleaningId,
        AggregateType = "Cleaning",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        CleaningId = cleaningId,
    };

    private static CleaningCompleted NewCompleted(Guid tenantId, Guid cleaningId, Guid propertyId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = cleaningId,
        AggregateType = "Cleaning",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        CleaningId = cleaningId,
        PropertyId = propertyId,
    };

    private static CleaningCancelled NewCancelled(Guid tenantId, Guid cleaningId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = cleaningId,
        AggregateType = "Cleaning",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        CleaningId = cleaningId,
    };

    // ---- CleaningCreated (the fan-out target this checkpoint's mandate requires evidence for) ----

    [Fact]
    public async Task CleaningCreated_inserts_a_new_row_with_the_real_ScheduledAtUtc()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId, now, scheduledAtUtc)));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry.Should().NotBeNull();
        entry!.PropertyId.Should().Be(propertyId);
        entry.Status.Should().Be("Pending");
        entry.ScheduledAtUtc.Should().Be(scheduledAtUtc);
    }

    [Fact]
    public async Task CleaningCreated_is_idempotent_on_redelivery_and_never_creates_a_duplicate_row()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var created = NewCreated(tenantId, cleaningId, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));

        (await CountEntriesAsync(tenantId, cleaningId)).Should().Be(1);
    }

    [Fact]
    public async Task CleaningStarted_updates_the_status_to_Started()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid(), t0)));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewStarted(tenantId, cleaningId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Started");
        entry.StartedAtUtc.Should().BeCloseTo(t0.AddMinutes(1), TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Checkpoint 2 mandate §8: CompletedAtUtc must trace to CleaningCompleted.Timestamp.</summary>
    [Fact]
    public async Task CleaningCompleted_sets_the_status_to_Completed_and_records_CompletedAtUtc()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId, t0)));

        var completedAt = t0.AddHours(1);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCompleted(tenantId, cleaningId, propertyId, completedAt)));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Completed");
        entry.CompletedAtUtc.Should().BeCloseTo(completedAt, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Checkpoint 2 mandate §7: CancelledAtUtc must trace to CleaningCancelled.Timestamp, and stale redelivery must never regress it.</summary>
    [Fact]
    public async Task CleaningCancelled_sets_the_status_to_Cancelled_and_records_CancelledAtUtc()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, Guid.NewGuid(), t0)));

        var cancelledAt = t0.AddMinutes(30);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCancelled(tenantId, cleaningId, cancelledAt)));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Cancelled");
        entry.CancelledAtUtc.Should().BeCloseTo(cancelledAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task An_out_of_order_older_CleaningCancelled_never_regresses_a_newer_already_applied_state()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, cleaningId, propertyId, t0)));

        var completedAt = t0.AddHours(2);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCompleted(tenantId, cleaningId, propertyId, completedAt)));

        // A stale Cancelled (Timestamp before the row's current LastEventAtUtc) arrives afterward.
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCancelled(tenantId, cleaningId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, cleaningId);
        entry!.Status.Should().Be("Completed", "an out-of-order older Cancelled must never regress an already-applied newer Completed state");
        entry.CancelledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_status_updating_event_is_invisible_when_the_message_scope_tenant_does_not_match_the_rows_owning_tenant_RLS_fail_closed()
    {
        using var host = BuildHost();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, ownerTenantId, s => s.HandleAsync(NewCreated(ownerTenantId, cleaningId, Guid.NewGuid(), t0)));

        await InvokeAsync(host, otherTenantId, s => s.HandleAsync(NewCancelled(ownerTenantId, cleaningId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(ownerTenantId, cleaningId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("Pending", "a mismatched tenant scope must never be able to update another tenant's row");
    }
}
