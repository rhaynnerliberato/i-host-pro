using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Dashboard.Tests.Integration;

/// <summary>
/// Preventive coverage for the four Dashboard projection bootstrap steps
/// (ADR-017; Fase 7, Incremento 2 — Dashboard &amp; Reporting Foundation,
/// Checkpoint 1, §16) — proves the same fresh-install/upgrade/idempotency
/// contract <c>PropertyProjectionBootstrapTests</c> already proves for
/// Housekeeping's own bootstrap step: a Reservation/Cleaning/Property/
/// CleaningOccurrence that existed BEFORE Dashboard's own consumer queues
/// were ever bound must be backfilled by this one-time, deployment-time
/// mechanism (RabbitMQ never replays history to a newly-bound queue), and
/// running it again must never duplicate or regress a row.
///
/// Also locks in the case-conversion facts driving each step's SQL:
/// <c>reservations.reservations.status</c>/<c>property_management.properties.status</c>
/// store the raw PascalCase enum name and are lowered to match
/// <c>ReservationStatusCodeMapper</c>/<c>PropertyStatusCodeMapper</c>'s
/// stable codes; <c>housekeeping.cleanings.status</c>/
/// <c>housekeeping.cleaning_occurrences.type</c> already equal their own
/// code mappers' values and are copied unchanged.
/// </summary>
public class DashboardProjectionBootstrapStepsTests : IClassFixture<DashboardProjectionBootstrapStepsTests.Fixture>
{
    private readonly Fixture _fixture;

    public DashboardProjectionBootstrapStepsTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fresh_install_with_no_pre_existing_source_data_backfills_nothing()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);

        await RunAllStepsAsync();

        (await CountReservationProjectionAsync(tenantId)).Should().Be(0);
        (await CountCleaningProjectionAsync(tenantId)).Should().Be(0);
        (await CountPropertyProjectionAsync(tenantId)).Should().Be(0);
        (await CountOccurrenceProjectionAsync(tenantId)).Should().Be(0);
    }

    [Fact]
    public async Task Backfills_pre_existing_source_data_correctly_and_is_idempotent_on_rerun()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);

        var propertyId = await SeedActivePropertyAsync(tenantId);
        var reservationId = await SeedCancelledReservationAsync(tenantId, propertyId);
        var cleaningId = await SeedPendingCleaningAsync(tenantId, propertyId);
        var occurrenceId = await SeedDamageOccurrenceAsync(tenantId, cleaningId);

        // Given: every source Bounded Context already has real data, but
        // Dashboard's own projections are empty — exactly the state any
        // pre-existing environment is in relative to a newly-deployed
        // Dashboard consumer.
        (await CountReservationProjectionAsync(tenantId)).Should().Be(0);
        (await CountCleaningProjectionAsync(tenantId)).Should().Be(0);
        (await CountPropertyProjectionAsync(tenantId)).Should().Be(0);
        (await CountOccurrenceProjectionAsync(tenantId)).Should().Be(0);

        // When: the upgrade/bootstrap mechanism runs.
        await RunAllStepsAsync();

        // Then: all four projections are correctly populated, including the
        // lowercase-vs-PascalCase status/type conversion each step applies
        // (or deliberately does not apply).
        await using (var dbContext = CreateDashboardDbContext(tenantId))
        await using (var transaction = await dbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(dbContext, tenantId);

            var reservation = await dbContext.ReservationProjection.SingleAsync(r => r.ReservationId == reservationId);
            reservation.PropertyId.Should().Be(propertyId);
            reservation.Status.Should().Be("cancelled");
            reservation.CheckInAt.Should().NotBeNull();
            reservation.CheckOutAt.Should().NotBeNull();
            reservation.CancelledAtUtc.Should().NotBeNull("the source reservation was already Cancelled before bootstrap ran");

            var cleaning = await dbContext.CleaningProjection.SingleAsync(c => c.CleaningId == cleaningId);
            cleaning.PropertyId.Should().Be(propertyId);
            cleaning.Status.Should().Be("Pending");
            cleaning.ScheduledAtUtc.Should().NotBeNull();

            var property = await dbContext.PropertyProjection.SingleAsync(p => p.PropertyId == propertyId);
            property.Status.Should().Be("active");

            var occurrence = await dbContext.OccurrenceProjection.SingleAsync(o => o.OccurrenceId == occurrenceId);
            occurrence.CleaningId.Should().Be(cleaningId);
            occurrence.Type.Should().Be("Damage");
        }

        // And: running the mechanism again — the real "upgrade runs twice"
        // scenario — inserts nothing new and regresses nothing (no
        // duplicate row, no primary-key violation, no state rollback).
        await RunAllStepsAsync();

        (await CountReservationProjectionAsync(tenantId)).Should().Be(1);
        (await CountCleaningProjectionAsync(tenantId)).Should().Be(1);
        (await CountPropertyProjectionAsync(tenantId)).Should().Be(1);
        (await CountOccurrenceProjectionAsync(tenantId)).Should().Be(1);
    }

    /// <summary>
    /// Checkpoint 2 mandate, §13/§21/§37: CancelledAtUtc must be backfilled
    /// from the real source timestamp, not a fabricated value — Reservations'
    /// <c>updated_at</c> (reliable because Cancel always touches it and
    /// Cancelled is terminal) and Housekeeping's own dedicated
    /// <c>cancelled_at_utc</c> column.
    /// </summary>
    [Fact]
    public async Task Backfills_CancelledAtUtc_from_the_real_source_timestamp_for_both_reservations_and_cleanings()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);

        var propertyId = await SeedActivePropertyAsync(tenantId);
        var cancelledAt = DateTimeOffset.UtcNow.AddDays(-2);
        var reservationId = await SeedCancelledReservationAsync(tenantId, propertyId, cancelledAt);
        var cleaningId = await SeedCancelledCleaningAsync(tenantId, propertyId, cancelledAt);

        await RunAllStepsAsync();

        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservation = await dbContext.ReservationProjection.SingleAsync(r => r.ReservationId == reservationId);
        reservation.CancelledAtUtc.Should().BeCloseTo(cancelledAt, TimeSpan.FromSeconds(1));

        var cleaning = await dbContext.CleaningProjection.SingleAsync(c => c.CleaningId == cleaningId);
        cleaning.CancelledAtUtc.Should().BeCloseTo(cancelledAt, TimeSpan.FromSeconds(1));
    }

    // ---- Bootstrap execution --------------------------------------------

    private async Task RunAllStepsAsync()
    {
        IProjectionBootstrapStep[] steps =
        [
            new DashboardReservationProjectionBootstrapStep(_fixture.MigratorConnectionString),
            new DashboardCleaningProjectionBootstrapStep(_fixture.MigratorConnectionString),
            new DashboardPropertyProjectionBootstrapStep(_fixture.MigratorConnectionString),
            new DashboardOccurrenceProjectionBootstrapStep(_fixture.MigratorConnectionString),
        ];

        foreach (var step in steps)
            await step.ExecuteAsync(NullLogger.Instance, CancellationToken.None);
    }

    // ---- Seeding ---------------------------------------------------------

    private async Task SeedTenantAsync(Guid tenantId)
    {
        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"t-{tenantId:N}"[..12]), "Test Tenant", DateTimeOffset.UtcNow);

        await using var dbContext = CreateIdentityDbContext();
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId)
    {
        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]), "Test Property", capacity: 4,
            condominiumId: null, address, DateTimeOffset.UtcNow);

        property.Activate(DateTimeOffset.UtcNow);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private async Task<Guid> SeedCancelledReservationAsync(Guid tenantId, Guid propertyId, DateTimeOffset? cancelledAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, propertyId, "Test Guest", null,
            now.AddDays(20), now.AddDays(24), guestCount: 2, now);
        reservation.Cancel(cancelledAt ?? now);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return reservation.Id;
    }

    private async Task<Guid> SeedCancelledCleaningAsync(Guid tenantId, Guid propertyId, DateTimeOffset cancelledAt)
    {
        var now = DateTimeOffset.UtcNow;
        var cleaning = Cleaning.Create(
            Guid.NewGuid(), tenantId, propertyId, reservationId: null, createdByUserId: Guid.NewGuid(), now,
            scheduledAtUtc: now.AddDays(1));
        cleaning.Cancel(cancelledAt);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateHousekeepingDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return cleaning.Id;
    }

    private async Task<Guid> SeedPendingCleaningAsync(Guid tenantId, Guid propertyId)
    {
        var now = DateTimeOffset.UtcNow;
        var cleaning = Cleaning.Create(
            Guid.NewGuid(), tenantId, propertyId, reservationId: null, createdByUserId: Guid.NewGuid(), now,
            scheduledAtUtc: now.AddDays(1));

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateHousekeepingDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return cleaning.Id;
    }

    private async Task<Guid> SeedDamageOccurrenceAsync(Guid tenantId, Guid cleaningId)
    {
        var occurrence = CleaningOccurrence.Create(
            Guid.NewGuid(), tenantId, cleaningId, OccurrenceType.Damage, description: null,
            registeredByUserId: Guid.NewGuid(), registeredAtUtc: DateTimeOffset.UtcNow);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateHousekeepingDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.CleaningOccurrences.Add(occurrence);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return occurrence.Id;
    }

    // ---- Assertions --------------------------------------------------

    private async Task<int> CountReservationProjectionAsync(Guid tenantId)
    {
        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.ReservationProjection.CountAsync(r => r.TenantId == tenantId);
    }

    private async Task<int> CountCleaningProjectionAsync(Guid tenantId)
    {
        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.CleaningProjection.CountAsync(c => c.TenantId == tenantId);
    }

    private async Task<int> CountPropertyProjectionAsync(Guid tenantId)
    {
        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.PropertyProjection.CountAsync(p => p.TenantId == tenantId);
    }

    private async Task<int> CountOccurrenceProjectionAsync(Guid tenantId)
    {
        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.OccurrenceProjection.CountAsync(o => o.TenantId == tenantId);
    }

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateIdentityDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, new TenantContext());
    }

    private PropertyManagementDbContext CreatePropertyManagementDbContext(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext ?? new TenantContext());
    }

    private ReservationsDbContext CreateReservationsDbContext(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;

        return new ReservationsDbContext(options, tenantContext ?? new TenantContext());
    }

    private HousekeepingDbContext CreateHousekeepingDbContext(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext ?? new TenantContext());
    }

    private DashboardDbContext CreateDashboardDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
            .Options;

        return new DashboardDbContext(options, tenantContext);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string MigratorRolePassword = "test_migrator_password";
        private const string AppRolePassword = "test_app_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;

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

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = "ihostpro_migrator",
                Password = MigratorRolePassword,
            };
            MigratorConnectionString = builder.ConnectionString;

            // Every schema the four Dashboard bootstrap steps either read
            // from (Reservations/Housekeeping/PropertyManagement) or write
            // to (Dashboard) must exist first — plus identity.tenants, the
            // platform-level catalog every step's per-tenant loop reads.
            await using (var identityDbContext = new IdentityDbContext(
                new DbContextOptionsBuilder<IdentityDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                    .Options,
                new TenantContext()))
            {
                await identityDbContext.Database.MigrateAsync();
            }

            await using (var pmDbContext = new PropertyManagementDbContext(
                new DbContextOptionsBuilder<PropertyManagementDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
                    .Options,
                new TenantContext()))
            {
                await pmDbContext.Database.MigrateAsync();
            }

            await using (var reservationsDbContext = new ReservationsDbContext(
                new DbContextOptionsBuilder<ReservationsDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
                    .Options,
                new TenantContext()))
            {
                await reservationsDbContext.Database.MigrateAsync();
            }

            await using (var housekeepingDbContext = new HousekeepingDbContext(
                new DbContextOptionsBuilder<HousekeepingDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
                    .Options,
                new TenantContext()))
            {
                await housekeepingDbContext.Database.MigrateAsync();
            }

            await using (var dashboardDbContext = new DashboardDbContext(
                new DbContextOptionsBuilder<DashboardDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
                    .Options,
                new TenantContext()))
            {
                await dashboardDbContext.Database.MigrateAsync();
            }
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }
}
