using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
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
/// Fase 7, Incremento 2 — Dashboard &amp; Reporting Foundation, Checkpoint 1,
/// §29 gate: drives <see cref="DashboardReservationProjectionSynchronizer"/>
/// directly (bypassing Wolverine's own RabbitMQ dispatch, but never
/// bypassing the real tenant-aware/RLS-protected PostgreSQL write path) to
/// prove the out-of-order delivery guard (an event only applies if its own
/// <c>Timestamp</c> is not earlier than the row's current
/// <c>LastEventAtUtc</c>) never regresses the projection — the explicit
/// proof §29 requires BEFORE this mechanism could be consolidated — plus
/// redelivery idempotency and RLS fail-closed isolation. Mirrors
/// <c>CleaningScheduleProjectionSynchronizerTests</c> (Reservations' own
/// precedent for this exact style of test) closely.
/// </summary>
public class DashboardReservationProjectionSynchronizerTests : IClassFixture<DashboardReservationProjectionSynchronizerTests.Fixture>
{
    private const string MainSchema = "platform_messaging";
    private const string DashboardOutboxSchema = "dashboard_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public DashboardReservationProjectionSynchronizerTests(Fixture fixture)
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

    /// <summary>
    /// Resolves the synchronizer from a fresh DI scope with the given tenant
    /// already set on <see cref="ITenantContext"/> — mirrors how a real
    /// consumed message's tenant-resolution middleware would populate it in
    /// <c>IHostPro.Worker</c>, one scope per message.
    /// </summary>
    private static async Task InvokeAsync(IHost host, Guid tenantContextTenantId, Func<DashboardReservationProjectionSynchronizer, Task> action)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantContextTenantId);
        var synchronizer = scope.ServiceProvider.GetRequiredService<DashboardReservationProjectionSynchronizer>();
        await action(synchronizer);
    }

    private async Task<DashboardReservationProjectionEntry?> ReadEntryAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.ReservationProjection.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId);

        await transaction.CommitAsync();
        return entry;
    }

    private async Task<int> CountEntriesAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var count = await dbContext.ReservationProjection
            .CountAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId);

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

    private static ReservationCreated NewCreated(
        Guid tenantId, Guid reservationId, Guid propertyId, DateTimeOffset timestamp,
        DateTimeOffset? checkInAt = null, DateTimeOffset? checkOutAt = null) => new()
    {
        TenantId = tenantId,
        AggregateId = reservationId,
        AggregateType = "Reservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        ReservationId = reservationId,
        PropertyId = propertyId,
        Status = "confirmed",
        CheckInAt = checkInAt,
        CheckOutAt = checkOutAt,
        Source = "manual",
    };

    private static ReservationUpdated NewUpdated(
        Guid tenantId, Guid reservationId, DateTimeOffset timestamp, DateTimeOffset? checkInAt, DateTimeOffset? checkOutAt) => new()
    {
        TenantId = tenantId,
        AggregateId = reservationId,
        AggregateType = "Reservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        ReservationId = reservationId,
        ChangedFields = ["check_in_at", "check_out_at"],
        CheckInAt = checkInAt,
        CheckOutAt = checkOutAt,
    };

    private static ReservationCancelled NewCancelled(Guid tenantId, Guid reservationId, DateTimeOffset timestamp) => new()
    {
        TenantId = tenantId,
        AggregateId = reservationId,
        AggregateType = "Reservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        ReservationId = reservationId,
        PropertyId = Guid.NewGuid(),
    };

    // ---- ReservationCreated -------------------------------------------------

    [Fact]
    public async Task ReservationCreated_inserts_a_new_row_with_the_real_CheckInAt_and_CheckOutAt()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var checkInAt = new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);
        var checkOutAt = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, reservationId, propertyId, now, checkInAt, checkOutAt)));

        var entry = await ReadEntryAsync(tenantId, reservationId);
        entry.Should().NotBeNull();
        entry!.PropertyId.Should().Be(propertyId);
        entry.Status.Should().Be("confirmed");
        entry.CheckInAt.Should().Be(checkInAt);
        entry.CheckOutAt.Should().Be(checkOutAt);
    }

    [Fact]
    public async Task ReservationCreated_is_idempotent_on_redelivery_and_never_creates_a_duplicate_row()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var created = NewCreated(tenantId, reservationId, Guid.NewGuid(), DateTimeOffset.UtcNow);

        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));
        await InvokeAsync(host, tenantId, s => s.HandleAsync(created));

        (await CountEntriesAsync(tenantId, reservationId)).Should().Be(1);
    }

    // ---- ReservationUpdated / ReservationCancelled ---------------------------

    [Fact]
    public async Task ReservationUpdated_with_a_newer_Timestamp_updates_CheckInAt_and_CheckOutAt()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(
            tenantId, reservationId, Guid.NewGuid(), t0, t0.AddDays(1), t0.AddDays(3))));

        var newCheckInAt = t0.AddDays(2);
        var newCheckOutAt = t0.AddDays(4);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewUpdated(tenantId, reservationId, t0.AddMinutes(1), newCheckInAt, newCheckOutAt)));

        var entry = await ReadEntryAsync(tenantId, reservationId);
        entry!.CheckInAt.Should().BeCloseTo(newCheckInAt, TimeSpan.FromMilliseconds(1));
        entry.CheckOutAt.Should().BeCloseTo(newCheckOutAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ReservationCancelled_with_a_newer_Timestamp_sets_status_to_cancelled_and_leaves_dates_untouched()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var checkInAt = t0.AddDays(1);
        var checkOutAt = t0.AddDays(3);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, reservationId, Guid.NewGuid(), t0, checkInAt, checkOutAt)));

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCancelled(tenantId, reservationId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, reservationId);
        entry!.Status.Should().Be("cancelled");
        entry.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromMilliseconds(1));
        entry.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_status_updating_event_for_a_Reservation_whose_ReservationCreated_was_never_projected_is_silently_ignored()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCancelled(tenantId, reservationId, DateTimeOffset.UtcNow)));

        (await ReadEntryAsync(tenantId, reservationId)).Should().BeNull(
            "a status-updating event before ReservationCreated must never fabricate a row on its own");
    }

    // ---- Out-of-order delivery guard (Checkpoint 0 decision, §29) -----------

    [Fact]
    public async Task An_out_of_order_older_ReservationUpdated_never_regresses_a_newer_already_applied_state()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(
            tenantId, reservationId, Guid.NewGuid(), t0, t0.AddDays(1), t0.AddDays(3))));

        // A genuinely newer update is applied first (as if it arrived before
        // an older, delayed redelivery/out-of-order message).
        var newerCheckInAt = t0.AddDays(5);
        var newerCheckOutAt = t0.AddDays(7);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(
            NewUpdated(tenantId, reservationId, t0.AddMinutes(10), newerCheckInAt, newerCheckOutAt)));

        // An OLDER event (Timestamp before the row's current LastEventAtUtc)
        // arrives afterward — the out-of-order guard must reject it,
        // never overwriting the already-applied newer state.
        var staleCheckInAt = t0.AddDays(1);
        var staleCheckOutAt = t0.AddDays(3);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(
            NewUpdated(tenantId, reservationId, t0.AddMinutes(1), staleCheckInAt, staleCheckOutAt)));

        var entry = await ReadEntryAsync(tenantId, reservationId);
        entry!.CheckInAt.Should().BeCloseTo(newerCheckInAt, TimeSpan.FromMilliseconds(1), "an out-of-order older event must never regress an already-applied newer state");
        entry.CheckOutAt.Should().BeCloseTo(newerCheckOutAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task An_out_of_order_older_ReservationCancelled_never_regresses_a_newer_already_applied_state()
    {
        using var host = BuildHost();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCreated(tenantId, reservationId, Guid.NewGuid(), t0)));

        // A newer update is applied first.
        var newCheckInAt = t0.AddDays(2);
        var newCheckOutAt = t0.AddDays(4);
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewUpdated(tenantId, reservationId, t0.AddMinutes(10), newCheckInAt, newCheckOutAt)));

        // An older Cancelled event (predating the update above) must never
        // apply — the row must remain "confirmed" with the newer dates.
        await InvokeAsync(host, tenantId, s => s.HandleAsync(NewCancelled(tenantId, reservationId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(tenantId, reservationId);
        entry!.Status.Should().Be("confirmed", "an out-of-order older Cancelled must never regress an already-applied newer state");
        entry.CheckInAt.Should().BeCloseTo(newCheckInAt, TimeSpan.FromMilliseconds(1));
        entry.CheckOutAt.Should().BeCloseTo(newCheckOutAt, TimeSpan.FromMilliseconds(1));
    }

    // ---- RLS fail-closed ------------------------------------------------

    [Fact]
    public async Task A_status_updating_event_is_invisible_when_the_message_scope_tenant_does_not_match_the_rows_owning_tenant_RLS_fail_closed()
    {
        using var host = BuildHost();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        await InvokeAsync(host, ownerTenantId, s => s.HandleAsync(NewCreated(ownerTenantId, reservationId, Guid.NewGuid(), t0)));

        // The event payload still claims the row's real TenantId, but the
        // DI-scoped ITenantContext (which drives the RLS session, exactly as
        // a real tenant-resolution-middleware-populated scope would) is
        // deliberately set to a DIFFERENT tenant — simulating a
        // misrouted/misresolved scope. RLS must make the row invisible
        // regardless of what the event's own TenantId column says.
        await InvokeAsync(host, otherTenantId, s => s.HandleAsync(
            NewCancelled(ownerTenantId, reservationId, t0.AddMinutes(1))));

        var entry = await ReadEntryAsync(ownerTenantId, reservationId);
        entry.Should().NotBeNull();
        entry!.Status.Should().Be("confirmed", "a mismatched tenant scope must never be able to update another tenant's row, even carrying the correct TenantId in the payload");
    }
}
