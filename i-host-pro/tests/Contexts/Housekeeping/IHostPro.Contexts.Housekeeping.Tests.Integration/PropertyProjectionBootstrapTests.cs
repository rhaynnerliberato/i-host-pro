using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Preventive coverage for <c>PropertyProjectionBootstrap</c> (Fase 7,
/// Incremento 1, Checkpoint 3) — proves the fresh-install/upgrade/
/// idempotency contract required before <c>CreateCleaning</c> can be
/// considered unblocked: a Property that already existed in
/// PropertyManagement BEFORE Housekeeping's own projection consumer ever ran
/// (the exact <c>property_not_found</c> defect registered in the Fase 7
/// homologação document, Checkpoint 2 §5.11) must be backfilled by this
/// one-time, deployment-time mechanism — never by a runtime cross-context
/// read from <c>CreateCleaningCommandHandler</c>, which this test never
/// touches.
/// </summary>
public class PropertyProjectionBootstrapTests : IClassFixture<PropertyProjectionBootstrapTests.Fixture>
{
    private readonly Fixture _fixture;

    public PropertyProjectionBootstrapTests(Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Backfills_a_pre_existing_active_property_and_is_idempotent_on_rerun()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedPropertyAsync(tenantId, activate: true);

        // Given: PropertyManagement already has an active Property, but
        // Housekeeping's own projection is empty — exactly the state any
        // pre-existing environment is in relative to the property_not_found
        // defect.
        (await CountProjectionRowsAsync(tenantId)).Should().Be(0);

        // When: the upgrade/bootstrap mechanism runs.
        await PropertyProjectionBootstrap.RunAsync(
            _fixture.MigratorConnectionString, NullLogger.Instance, CancellationToken.None);

        // Then: Housekeeping's projection now contains this Property, active
        // — and CreateCleaning's own real read port confirms it, the same
        // port CreateCleaningCommandHandler depends on.
        (await CountProjectionRowsAsync(tenantId)).Should().Be(1);

        await using (var readerDbContext = CreateHousekeepingDbContext(_fixture.MigratorConnectionString, tenantId))
        {
            var reader = new PropertyReferenceProjectionReader(readerDbContext);
            (await reader.IsKnownActivePropertyAsync(tenantId, propertyId, CancellationToken.None)).Should().BeTrue();
        }

        // And: running the mechanism again — the real "upgrade runs twice"
        // scenario — inserts nothing new and regresses nothing (no
        // duplicate row, no primary-key violation).
        await PropertyProjectionBootstrap.RunAsync(
            _fixture.MigratorConnectionString, NullLogger.Instance, CancellationToken.None);

        (await CountProjectionRowsAsync(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Fresh_install_with_no_pre_existing_properties_backfills_nothing()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);

        // Given: a brand-new tenant with zero properties (the real
        // fresh-install shape — the projection would otherwise be populated
        // purely by real-time PropertyCreated/PropertyActivated events, this
        // mechanism has nothing to do).
        await PropertyProjectionBootstrap.RunAsync(
            _fixture.MigratorConnectionString, NullLogger.Instance, CancellationToken.None);

        (await CountProjectionRowsAsync(tenantId)).Should().Be(0);
    }

    [Fact]
    public async Task Draft_property_is_backfilled_as_inactive()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        var propertyId = await SeedPropertyAsync(tenantId, activate: false);

        await PropertyProjectionBootstrap.RunAsync(
            _fixture.MigratorConnectionString, NullLogger.Instance, CancellationToken.None);

        await using var dbContext = CreateHousekeepingDbContext(_fixture.MigratorConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.PropertyProjection.SingleAsync(p => p.PropertyId == propertyId);
        entry.IsActive.Should().BeFalse();
    }

    // ---- Seeding -------------------------------------------------------

    private async Task SeedTenantAsync(Guid tenantId)
    {
        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"t-{tenantId:N}"[..12]), "Test Tenant", DateTimeOffset.UtcNow);

        await using var dbContext = CreateIdentityDbContext(_fixture.MigratorConnectionString);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedPropertyAsync(Guid tenantId, bool activate)
    {
        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]), "Test Property", capacity: 4,
            condominiumId: null, address, DateTimeOffset.UtcNow);

        if (activate)
            property.Activate(DateTimeOffset.UtcNow);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private async Task<int> CountProjectionRowsAsync(Guid tenantId)
    {
        await using var dbContext = CreateHousekeepingDbContext(_fixture.MigratorConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.PropertyProjection.CountAsync(p => p.TenantId == tenantId);
    }

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static IdentityDbContext CreateIdentityDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, new TenantContext());
    }

    private static PropertyManagementDbContext CreatePropertyManagementDbContext(
        string connectionString, ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext ?? new TenantContext());
    }

    private static HousekeepingDbContext CreateHousekeepingDbContext(string connectionString, Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
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

            // Identity is required because PropertyProjectionBootstrap reads
            // identity.tenants (the platform-level, non-RLS-protected tenant
            // catalog) to know which tenants to loop over — the same table
            // the real mechanism reads in production.
            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
                await identityDbContext.Database.MigrateAsync();

            await using (var pmDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
                await pmDbContext.Database.MigrateAsync();

            await using (var housekeepingDbContext = new HousekeepingDbContext(
                new DbContextOptionsBuilder<HousekeepingDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
                    .Options,
                new TenantContext()))
            {
                await housekeepingDbContext.Database.MigrateAsync();
            }
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }
}
