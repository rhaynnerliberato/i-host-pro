using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
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
/// Fase 7, Incremento 2 (Checkpoint 2, mandate §6/§20) — closes a second
/// coverage gap surfaced during the Checkpoint 2 audit: unlike
/// <c>DashboardReservationProjectionSynchronizer</c> (dedicated tests) and
/// <c>DashboardOccurrenceProjectionSynchronizer</c> (a real-transport gate),
/// <see cref="DashboardPropertyProjectionSynchronizer"/> had zero dedicated
/// coverage of its own — <c>PropertyEventsWorkerRoundTripTests</c> only ever
/// asserts on Housekeeping's own <c>PropertyProjection.IsActive</c>, never on
/// Dashboard's. Drives the synchronizer directly (bypassing RabbitMQ, never
/// bypassing the real tenant-aware/RLS-protected PostgreSQL write path),
/// mirroring <c>DashboardCleaningProjectionSynchronizerTests</c>'s own
/// structure — proportionally scoped to creation + one status transition +
/// idempotency + out-of-order guard + RLS fail-closed (not an exhaustive
/// four-transition proof: Activated/Deactivated/Archived all funnel through
/// the exact same <c>UpdateAsync</c> helper, so one representative transition
/// suffices to prove the mechanism).
/// </summary>
public class DashboardPropertyProjectionSynchronizerTests : IClassFixture<DashboardPropertyProjectionSynchronizerTests.Fixture>
{
    private const string MainSchema = "platform_messaging";
    private const string DashboardOutboxSchema = "dashboard_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public DashboardPropertyProjectionSynchronizerTests(Fixture fixture)
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

    private static async Task InvokeAsync(IHost host, Guid tenantContextTenantId, Func<DashboardPropertyProjectionSynchronizer, Task> action)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantContextTenantId);
        var synchronizer = scope.ServiceProvider.GetRequiredService<DashboardPropertyProjectionSynchronizer>();
        await action(synchronizer);
    }

    private async Task<DashboardPropertyProjectionEntry?> ReadEntryAsync(Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.PropertyProjection.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId);

        await transaction.CommitAsync();
        return entry;
    }

    private async Task<int> CountEntriesAsync(Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var count = await dbContext.PropertyProjection.CountAsync(p => p.TenantId == tenantId && p.PropertyId == propertyId);

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

    private static PropertyCreated NewCreated(Guid tenantId, Guid propertyId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = propertyId,
        AggregateType = "Property",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        PropertyId = propertyId,
        Status = "draft",
    };

    private static PropertyActivated NewActivated(Guid tenantId, Guid propertyId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = propertyId,
        AggregateType = "Property",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        PropertyId = propertyId,
    };

    private static PropertyDeactivated NewDeactivated(Guid tenantId, Guid propertyId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = propertyId,
        AggregateType = "Property",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        PropertyId = propertyId,
    };

    // ---- PropertyCreated (the fan-out target this checkpoint's mandate requires evidence for) ----

    [Fact]
    public async Task PropertyCreated_inserts_a_new_row_with_status_draft()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, propertyId, now)));

        var entry = await ReadEntryAsync(tenantId, propertyId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("draft");
    }

    [Fact]
    public async Task PropertyCreated_is_idempotent_on_redelivery_and_never_creates_a_duplicate_row()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var created = NewCreated(tenantId, propertyId, DateTimeOffset.UtcNow);

        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));

        (await CountEntriesAsync(tenantId, propertyId)).Should().Be(1);
    }

    [Fact]
    public async Task PropertyActivated_updates_the_status_to_active()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, propertyId, t0)));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewActivated(tenantId, propertyId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, propertyId);
        entry!.Status.Should().Be("active");
    }

    [Fact]
    public async Task A_status_updating_event_for_a_Property_whose_PropertyCreated_was_never_projected_is_silently_ignored()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewActivated(tenantId, propertyId, DateTimeOffset.UtcNow)));

        (await ReadEntryAsync(tenantId, propertyId)).Should().BeNull(
            "a status-updating event before PropertyCreated must never fabricate a row on its own");
    }

    [Fact]
    public async Task An_out_of_order_older_PropertyDeactivated_never_regresses_a_newer_already_applied_state()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, propertyId, t0)));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewActivated(tenantId, propertyId, t0.AddMinutes(10))));

        // A stale Deactivated (Timestamp before the row's current LastEventAtUtc) arrives afterward.
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewDeactivated(tenantId, propertyId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, propertyId);
        entry!.Status.Should().Be("active", "an out-of-order older Deactivated must never regress an already-applied newer Active state");
    }

    [Fact]
    public async Task A_status_updating_event_is_invisible_when_the_message_scope_tenant_does_not_match_the_rows_owning_tenant_RLS_fail_closed()
    {
        using var host = BuildHost();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, ownerTenantId, s => s.HandleAsync(NewCreated(ownerTenantId, propertyId, t0)));

        await InvokeAsync(host, otherTenantId, s => s.HandleAsync(NewActivated(ownerTenantId, propertyId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(ownerTenantId, propertyId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("draft", "a mismatched tenant scope must never be able to update another tenant's row");
    }
}
