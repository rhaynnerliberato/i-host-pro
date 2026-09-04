using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.HomologScenarioProvisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IHostPro.HomologScenarioProvisioning.Tests.Integration;

/// <summary>
/// Real-PostgreSQL coverage of <see cref="HomologScenarioProvisioner"/>
/// (CP5.3D-D corrective Decision Gate) - mirrors the fixture pattern already
/// established for IHostPro.TenantProvisioning's own tests (real roles, real
/// migrated schema, real RLS), across the three Bounded Contexts this tool
/// touches (PropertyManagement, Reservations, ExternalIntegrations).
/// </summary>
public class HomologScenarioProvisionerTests : IClassFixture<HomologScenarioProvisionerTests.Fixture>
{
    private readonly Fixture _fixture;

    public HomologScenarioProvisionerTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer Container { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await Container.StartAsync();

            var adminConnectionString = Container.GetConnectionString();

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
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            var tenantContext = new TenantContext();

            await using (var propertyDbContext = CreatePropertyDbContext(migratorConnectionString, tenantContext))
                await propertyDbContext.Database.MigrateAsync();
            await using (var reservationsDbContext = CreateReservationsDbContext(migratorConnectionString, tenantContext))
                await reservationsDbContext.Database.MigrateAsync();
            await using (var externalIntegrationsDbContext = CreateExternalIntegrationsDbContext(migratorConnectionString, tenantContext))
                await externalIntegrationsDbContext.Database.MigrateAsync();

            // housekeeping.property_projection's table is Housekeeping's own -
            // the fixture tool INSERTs into it directly (a real, documented
            // cross-context prerequisite), so it must exist even though this
            // test project never migrates the Housekeeping context itself.
            await using var adminConn = new NpgsqlConnection(adminConnectionString);
            await adminConn.OpenAsync();
            await using var createSchemaCommand = adminConn.CreateCommand();
            createSchemaCommand.CommandText = """
                CREATE SCHEMA IF NOT EXISTS housekeeping;
                CREATE TABLE IF NOT EXISTS housekeeping.property_projection (
                    tenant_id uuid NOT NULL,
                    property_id uuid NOT NULL,
                    is_active boolean NOT NULL,
                    PRIMARY KEY (tenant_id, property_id)
                );
                GRANT USAGE ON SCHEMA housekeeping TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON housekeeping.property_projection TO ihostpro_app;
                """;
            await createSchemaCommand.ExecuteNonQueryAsync();
        }

        public async Task DisposeAsync() => await Container.DisposeAsync();
    }

    private static PropertyManagementDbContext CreatePropertyDbContext(string connectionString, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options, tenantContext);

    private static ReservationsDbContext CreateReservationsDbContext(string connectionString, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options, tenantContext);

    private static ExternalIntegrationsDbContext CreateExternalIntegrationsDbContext(string connectionString, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options, tenantContext);

    private (HomologScenarioProvisioner Provisioner, PropertyManagementDbContext PropertyDb, ReservationsDbContext ReservationsDb, ExternalIntegrationsDbContext ExternalIntegrationsDb) CreateProvisioner()
    {
        var tenantContext = new TenantContext();
        var propertyDb = CreatePropertyDbContext(_fixture.AppConnectionString, tenantContext);
        var reservationsDb = CreateReservationsDbContext(_fixture.AppConnectionString, tenantContext);
        var externalIntegrationsDb = CreateExternalIntegrationsDbContext(_fixture.AppConnectionString, tenantContext);
        var provisioner = new HomologScenarioProvisioner(propertyDb, reservationsDb, externalIntegrationsDb, tenantContext, TimeProvider.System);
        return (provisioner, propertyDb, reservationsDb, externalIntegrationsDb);
    }

    /// <summary>
    /// The Testcontainers Postgres instance is shared across every test in
    /// this class (IClassFixture), but WhatsAppTenantRoute.PhoneNumberId is
    /// GLOBALLY unique by real design (exactly one route per synthetic
    /// phone_number_id, matching the real single-Homolog-tenant reality this
    /// tool exists for) - every test but the one that deliberately exercises
    /// that collision needs a clean slate for it first.
    /// </summary>
    private async Task ResetWhatsAppRouteAsync()
    {
        await using var db = CreateExternalIntegrationsDbContext(_fixture.AppConnectionString, new TenantContext());
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM external_integrations.whatsapp_tenant_routes WHERE phone_number_id = {HomologFixtureIdentifiers.PhoneNumberId}");
    }

    // ---- 1. First creation -------------------------------------------------

    [Fact]
    public async Task First_run_creates_the_property_reservation_and_whatsapp_route()
    {
        await ResetWhatsAppRouteAsync();
        var tenantId = Guid.NewGuid();
        var (provisioner, propertyDb, reservationsDb, externalIntegrationsDb) = CreateProvisioner();
        await using var p = propertyDb;
        await using var r = reservationsDb;
        await using var e = externalIntegrationsDb;

        var result = await provisioner.ProvisionAsync(tenantId, CancellationToken.None);

        result.PropertyCreated.Should().BeTrue();
        result.ReservationCreated.Should().BeTrue();
        result.RouteCreated.Should().BeTrue();
    }

    // ---- 2. Idempotency ------------------------------------------------

    [Fact]
    public async Task Running_it_twice_for_the_same_tenant_is_idempotent()
    {
        await ResetWhatsAppRouteAsync();
        var tenantId = Guid.NewGuid();

        var (firstProvisioner, firstP, firstR, firstE) = CreateProvisioner();
        await using (firstP) await using (firstR) await using (firstE)
            await firstProvisioner.ProvisionAsync(tenantId, CancellationToken.None);

        var (secondProvisioner, secondP, secondR, secondE) = CreateProvisioner();
        ScenarioResult secondResult;
        await using (secondP) await using (secondR) await using (secondE)
            secondResult = await secondProvisioner.ProvisionAsync(tenantId, CancellationToken.None);

        secondResult.PropertyCreated.Should().BeFalse();
        secondResult.ReservationCreated.Should().BeFalse();
        secondResult.RouteCreated.Should().BeFalse();

        await using var checkDb = CreatePropertyDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var tx = await checkDb.Database.BeginTransactionAsync();
        await checkDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var propertyCount = await checkDb.Properties.CountAsync(p => p.TenantId == tenantId);
        propertyCount.Should().Be(1, "idempotent re-run must never create a second Property");
    }

    // ---- 3/4. Correct tenant ownership --------------------------------

    [Fact]
    public async Task The_created_property_and_reservation_belong_to_the_requested_tenant()
    {
        await ResetWhatsAppRouteAsync();
        var tenantId = Guid.NewGuid();
        var (provisioner, propertyDb, reservationsDb, externalIntegrationsDb) = CreateProvisioner();
        await using var p = propertyDb;
        await using var r = reservationsDb;
        await using var e = externalIntegrationsDb;

        var result = await provisioner.ProvisionAsync(tenantId, CancellationToken.None);

        await using var checkPropertyDb = CreatePropertyDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var propertyTx = await checkPropertyDb.Database.BeginTransactionAsync();
        await checkPropertyDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var property = await checkPropertyDb.Properties.SingleAsync(x => x.Id == result.PropertyId);
        property.TenantId.Should().Be(tenantId);

        await using var checkReservationsDb = CreateReservationsDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var reservationsTx = await checkReservationsDb.Database.BeginTransactionAsync();
        await checkReservationsDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var reservation = await checkReservationsDb.Reservations.SingleAsync(x => x.Id == result.ReservationId);
        reservation.TenantId.Should().Be(tenantId);
        reservation.PropertyId.Should().Be(result.PropertyId);
    }

    // ---- 5/6/7. Fixture values the real webhook proof depends on -------

    [Fact]
    public async Task The_reservation_is_confirmed_and_uses_the_fixed_synthetic_guest_phone()
    {
        await ResetWhatsAppRouteAsync();
        var tenantId = Guid.NewGuid();
        var (provisioner, propertyDb, reservationsDb, externalIntegrationsDb) = CreateProvisioner();
        await using var p = propertyDb;
        await using var r = reservationsDb;
        await using var e = externalIntegrationsDb;

        var result = await provisioner.ProvisionAsync(tenantId, CancellationToken.None);

        await using var checkDb = CreateReservationsDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var tx = await checkDb.Database.BeginTransactionAsync();
        await checkDb.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var reservation = await checkDb.Reservations.SingleAsync(x => x.Id == result.ReservationId);

        reservation.Status.Should().Be(ReservationStatus.Confirmed);
        reservation.GuestPhone.Should().Be(HomologFixtureIdentifiers.GuestPhone);
    }

    [Fact]
    public async Task The_whatsapp_route_maps_the_fixed_synthetic_phone_number_id_to_the_requested_tenant()
    {
        await ResetWhatsAppRouteAsync();
        var tenantId = Guid.NewGuid();
        var (provisioner, propertyDb, reservationsDb, externalIntegrationsDb) = CreateProvisioner();
        await using var p = propertyDb;
        await using var r = reservationsDb;
        await using var e = externalIntegrationsDb;

        var result = await provisioner.ProvisionAsync(tenantId, CancellationToken.None);

        await using var checkDb = CreateExternalIntegrationsDbContext(_fixture.AppConnectionString, new TenantContext());
        var route = await checkDb.WhatsAppTenantRoutes.SingleAsync(x => x.Id == result.WhatsAppTenantRouteId);

        route.PhoneNumberId.Should().Be(HomologFixtureIdentifiers.PhoneNumberId);
        route.TenantId.Should().Be(tenantId);
    }

    // ---- 10. Refuses to silently reassign the route to a different tenant ----

    [Fact]
    public async Task Provisioning_for_a_different_tenant_after_the_route_already_exists_throws_instead_of_reassigning_it()
    {
        await ResetWhatsAppRouteAsync();
        var firstTenantId = Guid.NewGuid();
        var (firstProvisioner, firstP, firstR, firstE) = CreateProvisioner();
        await using (firstP) await using (firstR) await using (firstE)
            await firstProvisioner.ProvisionAsync(firstTenantId, CancellationToken.None);

        var secondTenantId = Guid.NewGuid();
        var (secondProvisioner, secondP, secondR, secondE) = CreateProvisioner();
        await using var sp = secondP;
        await using var sr = secondR;
        await using var se = secondE;

        var act = async () => await secondProvisioner.ProvisionAsync(secondTenantId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ITenantContext TenantScoped(Guid tenantId)
    {
        var context = new TenantContext();
        context.SetTenant(tenantId);
        return context;
    }
}
