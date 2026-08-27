using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.GuestOperations.Tests.Integration;

/// <summary>
/// Fase 10, Checkpoint 2 — Existing Reservation Upgrade Strategy (ADR-017;
/// ADR-024 amendment). Proves <see cref="GuestStayOperationBackfillBootstrapStep"/>'s
/// exact contract, mirroring <c>DashboardProjectionBootstrapStepsTests</c>'
/// own fresh-install/upgrade/idempotency shape: a Reservation that existed
/// BEFORE Guest Operations' own choreography consumer was ever bound could
/// never receive a real <c>ReservationCreated</c> delivery (RabbitMQ never
/// replays history to a newly-bound queue), so this one-time,
/// deployment-time mechanism is the only path such a Reservation can ever
/// get a <c>GuestStayOperation</c>.
/// </summary>
public class GuestStayOperationBackfillBootstrapStepTests : IClassFixture<GuestStayOperationBackfillBootstrapStepTests.Fixture>
{
    private readonly Fixture _fixture;

    public GuestStayOperationBackfillBootstrapStepTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_Confirmed_Reservation_without_a_GuestStayOperation_gets_one_Active_created()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var reservationId = await SeedReservationAsync(tenantId, propertyId, cancel: false);

        (await CountGuestStayOperationsAsync(tenantId)).Should().Be(0);

        await RunBackfillAsync();

        var operation = await GetGuestStayOperationAsync(tenantId, reservationId);
        operation.Should().NotBeNull();
        operation!.Status.Should().Be(GuestStayOperationStatus.Active);
        operation.PropertyId.Should().Be(propertyId);
        operation.CheckedInAtUtc.Should().BeNull();
        operation.CheckedOutAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task B_A_Cancelled_Reservation_without_a_GuestStayOperation_gets_none_created()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var reservationId = await SeedReservationAsync(tenantId, propertyId, cancel: true);

        await RunBackfillAsync();

        (await GetGuestStayOperationAsync(tenantId, reservationId)).Should().BeNull(
            "a Cancelled Reservation can never check in — backfilling a GuestStayOperation for it would be dead state");
    }

    [Fact]
    public async Task C_A_Confirmed_Reservation_with_an_existing_GuestStayOperation_is_a_no_op()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var reservationId = await SeedReservationAsync(tenantId, propertyId, cancel: false);
        var existingId = await SeedCheckedInGuestStayOperationAsync(tenantId, reservationId, propertyId);

        await RunBackfillAsync();

        var operation = await GetGuestStayOperationAsync(tenantId, reservationId);
        operation.Should().NotBeNull();
        operation!.Id.Should().Be(existingId, "backfill must never overwrite or duplicate an already-existing GuestStayOperation");
        operation.Status.Should().Be(GuestStayOperationStatus.CheckedIn,
            "backfill must never regress an already-progressed operation back to Active");
        (await CountGuestStayOperationsAsync(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task D_Multiple_tenants_each_receive_only_their_own_backfilled_data()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        await SeedTenantAsync(tenantAId);
        await SeedTenantAsync(tenantBId);

        var propertyAId = await SeedActivePropertyAsync(tenantAId);
        var propertyBId = await SeedActivePropertyAsync(tenantBId);
        var reservationAId = await SeedReservationAsync(tenantAId, propertyAId, cancel: false);
        var reservationBId = await SeedReservationAsync(tenantBId, propertyBId, cancel: false);

        await RunBackfillAsync();

        var operationA = await GetGuestStayOperationAsync(tenantAId, reservationAId);
        var operationB = await GetGuestStayOperationAsync(tenantBId, reservationBId);
        operationA.Should().NotBeNull();
        operationB.Should().NotBeNull();
        operationA!.TenantId.Should().Be(tenantAId);
        operationB!.TenantId.Should().Be(tenantBId);

        (await GetGuestStayOperationAsync(tenantAId, reservationBId)).Should().BeNull(
            "tenant A's RLS-scoped read must never see tenant B's backfilled row");
        (await GetGuestStayOperationAsync(tenantBId, reservationAId)).Should().BeNull(
            "tenant B's RLS-scoped read must never see tenant A's backfilled row");
    }

    [Fact]
    public async Task E_Running_the_backfill_a_second_time_inserts_zero_new_rows()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        await SeedReservationAsync(tenantId, propertyId, cancel: false);
        await SeedReservationAsync(tenantId, propertyId, cancel: true);

        await RunBackfillAsync();
        (await CountGuestStayOperationsAsync(tenantId)).Should().Be(1);

        await RunBackfillAsync();
        (await CountGuestStayOperationsAsync(tenantId)).Should().Be(1, "a second run must never duplicate an already-backfilled row");
    }

    // ---- Bootstrap execution --------------------------------------------

    private async Task RunBackfillAsync() =>
        await new GuestStayOperationBackfillBootstrapStep(_fixture.MigratorConnectionString)
            .ExecuteAsync(NullLogger.Instance, CancellationToken.None);

    // ---- Seeding ----------------------------------------------------------

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

    private async Task<Guid> SeedReservationAsync(Guid tenantId, Guid propertyId, bool cancel)
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, propertyId, "Test Guest", null,
            now.AddDays(-3), now.AddDays(-1), guestCount: 2, now);
        if (cancel)
            reservation.Cancel(now);

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

    private async Task<Guid> SeedCheckedInGuestStayOperationAsync(Guid tenantId, Guid reservationId, Guid propertyId)
    {
        var now = DateTimeOffset.UtcNow;
        var operation = GuestStayOperation.Create(Guid.NewGuid(), tenantId, reservationId, propertyId, now.AddDays(-2));
        operation.CheckIn(now.AddDays(-1));

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateGuestOperationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.GuestStayOperations.Add(operation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return operation.Id;
    }

    // ---- Assertions --------------------------------------------------

    private async Task<int> CountGuestStayOperationsAsync(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateGuestOperationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.GuestStayOperations.CountAsync(o => o.TenantId == tenantId);
    }

    private async Task<GuestStayOperation?> GetGuestStayOperationAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateGuestOperationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.GuestStayOperations.SingleOrDefaultAsync(
            o => o.TenantId == tenantId && o.ReservationId == reservationId);
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

    private GuestOperationsDbContext CreateGuestOperationsDbContext(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;

        return new GuestOperationsDbContext(options, tenantContext ?? new TenantContext());
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

            // Every schema the backfill step either reads from (Reservations)
            // or writes to (GuestOperations) must exist first — plus
            // identity.tenants, the platform-level catalog the step's own
            // per-tenant loop reads. property_management is migrated too
            // since Reservation.Create requires a real PropertyId.
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

            await using (var guestOperationsDbContext = new GuestOperationsDbContext(
                new DbContextOptionsBuilder<GuestOperationsDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
                    .Options,
                new TenantContext()))
            {
                await guestOperationsDbContext.Database.MigrateAsync();
            }
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }
}
