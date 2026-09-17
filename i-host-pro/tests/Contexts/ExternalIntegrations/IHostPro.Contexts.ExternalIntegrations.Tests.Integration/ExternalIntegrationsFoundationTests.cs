using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Exercises the External Integrations Bounded Context's physical
/// foundation against a real PostgreSQL instance (Testcontainers): migration
/// application/idempotency, Row-Level Security (fail-closed) on
/// <c>whatsapp_integrations</c>, the application role's lack of DDL/BYPASSRLS
/// privileges, the one-integration-per-tenant unique index, and the absence
/// of any raw secret value ever persisted — only opaque references. Mirrors
/// <c>ConfigurationFoundationTests</c>'s structure exactly, scoped to what
/// Checkpoint 2.1 actually needs (no messaging schema — External
/// Integrations publishes no Integration Event yet).
/// </summary>
public class ExternalIntegrationsFoundationTests : IClassFixture<ExternalIntegrationsFoundationTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ExternalIntegrationsFoundationTests(Fixture fixture)
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

            await using var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }

    // ---- Migration ----

    [Fact]
    public async Task Migration_applies_cleanly_and_creates_the_expected_table()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'external_integrations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain("whatsapp_integrations");
    }

    [Fact]
    public async Task Migration_is_idempotent_on_a_second_run()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var act = async () => await dbContext.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Row-Level Security (fail-closed) ----

    [Fact]
    public async Task Correct_tenant_sees_its_own_integration()
    {
        var (tenantId, integrationId) = await SeedIntegrationAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var integrations = await dbContext.WhatsAppIntegrations.ToListAsync();

        integrations.Should().ContainSingle(w => w.Id == integrationId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_and_cannot_alter_them()
    {
        var (_, integrationId) = await SeedIntegrationAsync();
        var (unrelatedTenantId, _) = await SeedIntegrationAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.WhatsAppIntegrations.Where(w => w.Id == integrationId).ToListAsync();
        visible.Should().BeEmpty();

        // FORCE ROW LEVEL SECURITY means an UPDATE targeting a row this
        // session cannot see affects zero rows — never an error, never a
        // cross-tenant write.
        await using (var connection = new NpgsqlConnection(_appConnectionString))
        {
            await connection.OpenAsync();
            await using var rawTransaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{unrelatedTenantId:D}'");

            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE external_integrations.whatsapp_integrations SET waba_id = 'hacked' WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", integrationId);
            var affected = await updateCommand.ExecuteNonQueryAsync();

            affected.Should().Be(0);
        }
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedIntegrationAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM external_integrations.whatsapp_integrations"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.WhatsAppIntegrations.Add(integration);

        // app.tenant_id was never set on this transaction.
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ENABLE_and_FORCE_row_level_security_are_active()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'whatsapp_integrations' AND relnamespace = 'external_integrations'::regnamespace";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    // ---- Application role privileges ----

    [Fact]
    public async Task App_role_cannot_create_alter_or_drop_tables()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var createAct = async () => await ExecuteAsync(connection, "CREATE TABLE external_integrations.hack (id uuid PRIMARY KEY)");
        await createAct.Should().ThrowAsync<PostgresException>();

        var alterAct = async () => await ExecuteAsync(connection, "ALTER TABLE external_integrations.whatsapp_integrations ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE external_integrations.whatsapp_integrations");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_the_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, "ALTER TABLE external_integrations.whatsapp_integrations DISABLE ROW LEVEL SECURITY");

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_does_not_have_BYPASSRLS()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var bypassRls = (bool)(await ExecuteScalarAsync(
            connection, "SELECT rolbypassrls FROM pg_roles WHERE rolname = 'ihostpro_app'"))!;

        bypassRls.Should().BeFalse("the application role must never be able to bypass Row-Level Security");
    }

    // ---- One integration per tenant (CP2.1 mandate §15) ----

    [Fact]
    public async Task A_second_integration_for_the_same_tenant_is_rejected_by_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.WhatsAppIntegrations.Add(WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.WhatsAppIntegrations.Add(WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("exactly one WhatsApp integration is allowed per tenant in the MVP");
    }

    // ---- Secret references, never a raw secret value ----

    [Fact]
    public async Task A_new_integration_is_disabled_by_default()
    {
        var (tenantId, integrationId) = await SeedIntegrationAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var integration = await dbContext.WhatsAppIntegrations.SingleAsync(w => w.Id == integrationId);

        integration.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_secret_reference_column_only_ever_stores_the_opaque_reference_string()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
        integration.UpdateConfiguration("waba-1", "phone-1", "my-opaque-access-token-reference", null, null, DateTimeOffset.UtcNow);
        dbContext.WhatsAppIntegrations.Add(integration);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var readTransaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId:D}'");
        var storedValue = (string)(await ExecuteScalarAsync(
            connection, $"SELECT access_token_secret_reference FROM external_integrations.whatsapp_integrations WHERE id = '{integration.Id:D}'"))!;

        storedValue.Should().Be("my-opaque-access-token-reference",
            "only the caller-assigned opaque reference is ever persisted — never a real secret value, since no real secret ever reaches this boundary");
    }

    // ---- WhatsAppTemplateMapping (Fase 9, Checkpoint 2.2) ----

    [Fact]
    public async Task Migration_creates_the_whatsapp_template_mappings_table()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'external_integrations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain("whatsapp_template_mappings");
    }

    [Fact]
    public async Task ENABLE_and_FORCE_row_level_security_are_active_on_whatsapp_template_mappings()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'whatsapp_template_mappings' AND relnamespace = 'external_integrations'::regnamespace";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    [Fact]
    public async Task Correct_tenant_sees_its_own_template_mapping()
    {
        var (tenantId, mappingId) = await SeedTemplateMappingAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var mappings = await dbContext.WhatsAppTemplateMappings.ToListAsync();

        mappings.Should().ContainSingle(m => m.Id == mappingId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_for_another_tenants_template_mapping()
    {
        var (_, mappingId) = await SeedTemplateMappingAsync();
        var (unrelatedTenantId, _) = await SeedTemplateMappingAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.WhatsAppTemplateMappings.Where(m => m.Id == mappingId).ToListAsync();
        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_mapping_for_the_same_tenant_and_templateKey_is_rejected_by_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.WhatsAppTemplateMappings.Add(WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), tenantId, "RESERVATION_CONFIRMATION", "name-1", "pt_BR", ["CheckInDate"], DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.WhatsAppTemplateMappings.Add(WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), tenantId, "RESERVATION_CONFIRMATION", "name-2", "en_US", ["CheckInDate"], DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("exactly one mapping is allowed per tenant+TemplateKey");
    }

    [Fact]
    public async Task The_parameterOrder_round_trips_through_the_jsonb_column()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), tenantId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR",
            ["GuestName", "CheckInDate"], DateTimeOffset.UtcNow);
        dbContext.WhatsAppTemplateMappings.Add(mapping);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var readDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var readTransaction = await readDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(readDbContext, tenantId);
        var reloaded = await readDbContext.WhatsAppTemplateMappings.AsNoTracking().SingleAsync(m => m.Id == mapping.Id);

        reloaded.ParameterOrder.Should().Equal("GuestName", "CheckInDate");
    }

    private async Task<(Guid TenantId, Guid MappingId)> SeedTemplateMappingAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), tenantId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR",
            ["CheckInDate"], DateTimeOffset.UtcNow);
        dbContext.WhatsAppTemplateMappings.Add(mapping);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, mapping.Id);
    }

    // ---- WhatsAppTenantRoute (Fase 9, Checkpoint 2.3.2) — global, non-tenant-owned ----

    [Fact]
    public async Task Migration_creates_the_whatsapp_tenant_routes_table()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'external_integrations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain("whatsapp_tenant_routes");
    }

    [Fact]
    public async Task Row_Level_Security_is_NOT_enabled_on_whatsapp_tenant_routes()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'whatsapp_tenant_routes' AND relnamespace = 'external_integrations'::regnamespace";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeFalse(
            "this table exists specifically to answer \"which tenant\" BEFORE a TenantId is known — RLS would defeat its entire purpose (ADR-022)");
        reader.GetBoolean(1).Should().BeFalse();
    }

    [Fact]
    public async Task A_route_created_for_one_tenant_is_visible_from_a_DIFFERENT_tenants_context()
    {
        // Deliberately the opposite assertion of WhatsAppIntegration's own
        // cross-tenant test above — global visibility here is the intended
        // design, not a leak (ADR-022 items 10-12).
        var tenantId = Guid.NewGuid();
        await SeedRouteAsync(tenantId, "global-visible-phone");

        var unrelatedTenantContext = new TenantContext();
        unrelatedTenantContext.SetTenant(Guid.NewGuid());
        await using var dbContext = CreateDbContext(_appConnectionString, unrelatedTenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantContext.TenantId!.Value);

        var visible = await dbContext.WhatsAppTenantRoutes.SingleOrDefaultAsync(r => r.PhoneNumberId == "global-visible-phone");

        visible.Should().NotBeNull("the routing directory must be readable before any tenant is resolved, by design");
        visible!.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Two_tenants_cannot_share_the_same_PhoneNumberId()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), "shared-phone", Guid.NewGuid(), DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), "shared-phone", Guid.NewGuid(), DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("PhoneNumberId must be globally unique across all tenants");
    }

    [Fact]
    public async Task A_tenant_cannot_have_two_active_routes()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), "phone-a", tenantId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), "phone-b", tenantId, DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("exactly one active route is allowed per tenant");
    }

    [Fact]
    public async Task App_role_cannot_create_alter_or_drop_the_routing_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var alterAct = async () => await ExecuteAsync(connection, "ALTER TABLE external_integrations.whatsapp_tenant_routes ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE external_integrations.whatsapp_tenant_routes");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_can_select_insert_update_and_delete_on_the_routing_table_without_any_tenant_context()
    {
        var tenantId = Guid.NewGuid();
        await SeedRouteAsync(tenantId, "app-role-crud-phone");

        // No SetTenant call anywhere below — proves this table's CRUD access
        // is intentionally unconditional, unlike every tenant-owned table.
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());

        var route = await dbContext.WhatsAppTenantRoutes.SingleAsync(r => r.PhoneNumberId == "app-role-crud-phone");
        route.UpdatePhoneNumberId("app-role-crud-phone-updated", DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();

        dbContext.WhatsAppTenantRoutes.Remove(route);
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Updating_a_tenants_PhoneNumberId_invalidates_the_old_route_and_activates_the_new_one()
    {
        var tenantId = Guid.NewGuid();
        var routeId = await SeedRouteAsync(tenantId, "old-route-phone");

        await using (var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext()))
        {
            var route = await dbContext.WhatsAppTenantRoutes.SingleAsync(r => r.Id == routeId);
            route.UpdatePhoneNumberId("new-route-phone", DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        await using var readDbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        var oldStillResolves = await readDbContext.WhatsAppTenantRoutes.AnyAsync(r => r.PhoneNumberId == "old-route-phone");
        var newResolves = await readDbContext.WhatsAppTenantRoutes.SingleOrDefaultAsync(r => r.PhoneNumberId == "new-route-phone");

        oldStillResolves.Should().BeFalse("the old PhoneNumberId must no longer resolve anything once reconfigured");
        newResolves.Should().NotBeNull();
        newResolves!.TenantId.Should().Be(tenantId);
    }

    /// <summary>
    /// Fase 9, Checkpoint 2.3.2 mandate §38: proves the atomicity
    /// ConfigureWhatsAppIntegrationCommandHandler relies on — a
    /// WhatsAppIntegration write and its WhatsAppTenantRoute write share one
    /// DbContext/one SaveChangesAsync, so a failure before commit rolls back
    /// BOTH, never leaving one persisted without the other.
    /// </summary>
    [Fact]
    public async Task Integration_and_route_writes_roll_back_together_on_failure()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.WhatsAppIntegrations.Add(WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow));
        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), "rollback-phone", tenantId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        // Simulates a failure discovered after both writes were flushed to
        // this transaction but before it commits — TenantAwareUnitOfWork
        // would roll back here on any exception from the handler.
        await transaction.RollbackAsync();

        var verifyTenantContext = new TenantContext();
        verifyTenantContext.SetTenant(tenantId);
        await using var verifyDbContext = CreateDbContext(_migratorConnectionString, verifyTenantContext);
        await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(verifyDbContext, tenantId);

        var integrationExists = await verifyDbContext.WhatsAppIntegrations.AnyAsync(w => w.TenantId == tenantId);
        var routeExists = await verifyDbContext.WhatsAppTenantRoutes.AnyAsync(r => r.TenantId == tenantId);

        integrationExists.Should().BeFalse("rollback must undo the WhatsAppIntegration write");
        routeExists.Should().BeFalse("rollback must undo the WhatsAppTenantRoute write in the same transaction");
    }

    private async Task<Guid> SeedRouteAsync(Guid tenantId, string phoneNumberId)
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        var route = WhatsAppTenantRoute.Create(Guid.NewGuid(), phoneNumberId, tenantId, DateTimeOffset.UtcNow);
        dbContext.WhatsAppTenantRoutes.Add(route);
        await dbContext.SaveChangesAsync();
        return route.Id;
    }

    // ---- Airbnb Email Bridge (persistence gate) ----

    [Fact]
    public async Task Migration_creates_the_airbnb_email_bridge_tables()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'external_integrations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain([
            "airbnb_email_mailbox_connections", "airbnb_email_sync_states", "airbnb_email_message_receipts",
        ]);
    }

    [Theory]
    [InlineData("airbnb_email_mailbox_connections")]
    [InlineData("airbnb_email_sync_states")]
    [InlineData("airbnb_email_message_receipts")]
    public async Task ENABLE_and_FORCE_row_level_security_are_active_on_airbnb_email_bridge_tables(string tableName)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = @tableName AND relnamespace = 'external_integrations'::regnamespace";
        command.Parameters.AddWithValue("tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    [Fact]
    public async Task Correct_tenant_sees_its_own_mailbox_connection()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var connections = await dbContext.AirbnbEmailMailboxConnections.ToListAsync();

        connections.Should().ContainSingle(c => c.Id == connectionId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_for_another_tenants_mailbox_connection()
    {
        var (_, connectionId) = await SeedMailboxConnectionAsync();
        var (unrelatedTenantId, _) = await SeedMailboxConnectionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.AirbnbEmailMailboxConnections.Where(c => c.Id == connectionId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_mailbox_connection_for_the_same_tenant_is_rejected_by_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.AirbnbEmailMailboxConnections.Add(AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.AirbnbEmailMailboxConnections.Add(AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("exactly one Airbnb email mailbox is allowed per tenant in the MVP");
    }

    [Fact]
    public async Task A_second_message_receipt_with_the_same_graph_message_id_for_the_same_tenant_is_rejected_by_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        dbContext.AirbnbEmailMessageReceipts.Add(
            AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), tenantId, "graph-message-1", null, now, now));
        await dbContext.SaveChangesAsync();

        dbContext.AirbnbEmailMessageReceipts.Add(
            AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), tenantId, "graph-message-1", null, now, now));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("a Graph delta replay must never be recorded twice for the same tenant");
    }

    [Fact]
    public async Task A_second_sync_state_for_the_same_connection_and_folder_is_rejected_by_the_unique_index()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.AirbnbEmailSyncStates.Add(
            AirbnbEmailSyncState.Create(Guid.NewGuid(), tenantId, connectionId, "inbox", DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync();

        dbContext.AirbnbEmailSyncStates.Add(
            AirbnbEmailSyncState.Create(Guid.NewGuid(), tenantId, connectionId, "inbox", DateTimeOffset.UtcNow));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("exactly one cursor is allowed per (tenant, connection, folder)");
    }

    [Fact]
    public async Task Deleting_a_mailbox_connection_referenced_by_a_sync_state_is_rejected()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using (var seedDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var seedTransaction = await seedDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(seedDbContext, tenantId);
            seedDbContext.AirbnbEmailSyncStates.Add(
                AirbnbEmailSyncState.Create(Guid.NewGuid(), tenantId, connectionId, "inbox", DateTimeOffset.UtcNow));
            await seedDbContext.SaveChangesAsync();
            await seedTransaction.CommitAsync();
        }

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var connection = await dbContext.AirbnbEmailMailboxConnections.SingleAsync(c => c.Id == connectionId);
        dbContext.AirbnbEmailMailboxConnections.Remove(connection);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("disconnecting must never silently orphan or cascade-delete sync history");
    }

    /// <summary>
    /// Proves the optimistic-concurrency protection the token cache needs
    /// (Fase 9 review item 7): two writers that both loaded the same
    /// connection before either saved must not silently overwrite each
    /// other — the second SaveChangesAsync must fail, never last-write-wins.
    /// </summary>
    [Fact]
    public async Task Concurrent_token_cache_updates_the_second_writer_fails_instead_of_overwriting()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var firstDbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var firstTransaction = await firstDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(firstDbContext, tenantId);
        var firstView = await firstDbContext.AirbnbEmailMailboxConnections.SingleAsync(c => c.Id == connectionId);

        await using var secondDbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var secondTransaction = await secondDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(secondDbContext, tenantId);
        var secondView = await secondDbContext.AirbnbEmailMailboxConnections.SingleAsync(c => c.Id == connectionId);

        firstView.UpdateTokenCache([1], DateTimeOffset.UtcNow);
        await firstDbContext.SaveChangesAsync();
        await firstTransaction.CommitAsync();

        secondView.UpdateTokenCache([2], DateTimeOffset.UtcNow);
        var act = async () => await secondDbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "the xmin-based concurrency token must reject a write based on stale data instead of overwriting the first writer's update");
    }

    [Fact]
    public async Task PostgresAirbnbEmailTokenCacheStore_encrypts_at_rest_and_round_trips_the_plaintext_cache()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();
        var plaintext = "msal-token-cache-payload"u8.ToArray();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();
        var protector = new AesGcmTokenCacheProtector(configuration);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var writeDbContext = CreateDbContext(_appConnectionString, tenantContext))
        {
            // No externally-managed transaction here (unlike the old version
            // of this test): PostgresAirbnbEmailTokenCacheStore now opens its
            // own tenant-scoped transaction internally, exactly like the
            // real Worker background-service call path (no ambient
            // transaction/HTTP request pipeline available there either).
            var unitOfWork = new AirbnbEmailUnitOfWork(writeDbContext, tenantContext);
            var store = new PostgresAirbnbEmailTokenCacheStore(writeDbContext, unitOfWork, protector, TimeProvider.System);
            await store.SaveAsync(tenantId, plaintext, CancellationToken.None);
        }

        await using (var connection = new NpgsqlConnection(_migratorConnectionString))
        {
            await connection.OpenAsync();
            await using var readTransaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId:D}'");
            var storedBytes = (byte[])(await ExecuteScalarAsync(
                connection, $"SELECT token_cache_blob FROM external_integrations.airbnb_email_mailbox_connections WHERE id = '{connectionId:D}'"))!;

            storedBytes.Should().NotEqual(plaintext, "the stored bytes must be ciphertext, never the plaintext cache");
        }

        await using var readDbContext = CreateDbContext(_appConnectionString, tenantContext);
        var readUnitOfWork = new AirbnbEmailUnitOfWork(readDbContext, tenantContext);
        var readStore = new PostgresAirbnbEmailTokenCacheStore(readDbContext, readUnitOfWork, protector, TimeProvider.System);
        var loaded = await readStore.LoadAsync(tenantId, CancellationToken.None);

        loaded.Should().Equal(plaintext);
    }

    /// <summary>
    /// Regression test for a real bug found by the Automatic Publication
    /// Controlled Auto-Activation Safety Smoke: <see cref="MsalAirbnbEmailAuthenticator"/>
    /// used to call <see cref="IAirbnbEmailMailboxConnectionRepository.GetForCurrentTenantAsync"/>
    /// directly, with no <see cref="IAirbnbEmailUnitOfWork"/> around it. That
    /// is harmless from an ASP.NET Core request (nothing in this codebase
    /// currently wraps it in an ambient transaction either, so this was
    /// already latent there too), but from <c>AirbnbEmailDeltaPollingBackgroundService</c>
    /// (Worker) there is no ambient transaction at all — PostgreSQL's
    /// Row-Level Security then silently hid an existing, genuinely connected
    /// row (no <c>SET LOCAL app.tenant_id</c> was ever applied), which the
    /// authenticator misread as "mailbox not connected" and the caller then
    /// persisted as a false <c>AuthorizationStatus.Error</c>. This test
    /// proves the same repository call, invoked exactly as the Worker calls
    /// it today (a fresh scope, tenant context set, no ambient transaction),
    /// now finds the row once wrapped in <see cref="IAirbnbEmailUnitOfWork.ExecuteAsync{TResult}"/>.
    /// </summary>
    [Fact]
    public async Task GetForCurrentTenantAsync_finds_a_real_connection_through_the_unit_of_work_with_no_ambient_transaction()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var repository = new AirbnbEmailMailboxConnectionRepository(dbContext);
        var unitOfWork = new AirbnbEmailUnitOfWork(dbContext, tenantContext);

        // Deliberately no Database.BeginTransactionAsync()/SetTenantAsync
        // here — this is the exact shape of AirbnbEmailDeltaPollingBackgroundService's
        // own call: a fresh DI scope with tenant context set, nothing else.
        var found = await unitOfWork.ExecuteAsync(
            () => repository.GetForCurrentTenantAsync(CancellationToken.None), CancellationToken.None);

        found.Should().NotBeNull(
            "a real, existing connection must remain visible under RLS once the unit of work sets app.tenant_id, even with no externally-managed ambient transaction");
        found!.Id.Should().Be(connectionId);
    }

    /// <summary>
    /// Companion to the regression test above: proves the historical bug
    /// really was RLS hiding the row, not something else — calling the
    /// repository directly (the old, buggy shape, with no unit of work at
    /// all) against the same real connection returns null even though the
    /// row genuinely exists.
    /// </summary>
    [Fact]
    public async Task GetForCurrentTenantAsync_returns_null_under_RLS_without_a_tenant_scoped_transaction()
    {
        var (tenantId, _) = await SeedMailboxConnectionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var repository = new AirbnbEmailMailboxConnectionRepository(dbContext);

        var found = await repository.GetForCurrentTenantAsync(CancellationToken.None);

        found.Should().BeNull("without SET LOCAL app.tenant_id inside an open transaction, RLS hides even a genuinely existing row");
    }

    /// <summary>
    /// The other safe outcome required alongside the fix: if tenant context
    /// was never resolved at all (a real configuration/infrastructure
    /// problem), the unit of work must fail loudly with
    /// <see cref="TenantContextNotResolvedException"/> — never silently
    /// return null and be misread as "mailbox not connected".
    /// </summary>
    [Fact]
    public async Task GetForCurrentTenantAsync_through_the_unit_of_work_throws_when_tenant_context_was_never_resolved()
    {
        await SeedMailboxConnectionAsync();

        var unresolvedTenantContext = new TenantContext();
        await using var dbContext = CreateDbContext(_appConnectionString, unresolvedTenantContext);
        var repository = new AirbnbEmailMailboxConnectionRepository(dbContext);
        var unitOfWork = new AirbnbEmailUnitOfWork(dbContext, unresolvedTenantContext);

        var act = async () => await unitOfWork.ExecuteAsync(
            () => repository.GetForCurrentTenantAsync(CancellationToken.None), CancellationToken.None);

        await act.Should().ThrowAsync<TenantContextNotResolvedException>(
            "an unresolved tenant context must fail loudly as an infrastructure error, never silently look like a disconnected mailbox");
    }

    /// <summary>
    /// A minimal <see cref="IAirbnbEmailAuthenticator"/> stand-in for the
    /// Connect/Disconnect handler regression tests below: on success it
    /// mirrors what the real MsalAirbnbEmailAuthenticator does (opens its own
    /// tenant-scoped transaction and calls <see cref="AirbnbEmailMailboxConnection.Connect"/>)
    /// against a REAL, already-seeded connection row - proving the handler's
    /// own subsequent read (in a separate transaction) does not nest inside
    /// this one.
    /// </summary>
    private sealed class SucceedingAirbnbEmailAuthenticatorStub(
        IAirbnbEmailMailboxConnectionRepository repository, IAirbnbEmailUnitOfWork unitOfWork) : IAirbnbEmailAuthenticator
    {
        public async Task<AirbnbEmailAuthenticationOutcome> ConnectInteractiveAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            await unitOfWork.ExecuteAsync(async () =>
            {
                var connection = await repository.GetForCurrentTenantAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Connection row must be seeded before calling this stub.");
                connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", DateTimeOffset.UtcNow);
                return true;
            }, cancellationToken);

            return AirbnbEmailAuthenticationOutcome.Success("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read");
        }

        public Task<AirbnbEmailSilentAcquisitionOutcome> AcquireTokenSilentAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Airbnb Email Bridge Audit Behavior DI Hardening gate: proves the fix
    /// for the latent RLS bug found while auditing that gate -
    /// ConnectAirbnbEmailMailboxCommandHandler's post-authentication read now
    /// runs inside its own IAirbnbEmailUnitOfWork transaction, so it finds
    /// the real, RLS-protected row the stub authenticator's OWN (separate,
    /// already-committed) transaction just wrote - and does not throw
    /// NestedUnitOfWorkException doing so.
    /// </summary>
    [Fact]
    public async Task ConnectAirbnbEmailMailboxCommandHandler_reads_the_connection_after_authentication_under_real_RLS_without_nesting()
    {
        var (tenantId, _) = await SeedMailboxConnectionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var repository = new AirbnbEmailMailboxConnectionRepository(dbContext);
        var unitOfWork = new AirbnbEmailUnitOfWork(dbContext, tenantContext);
        var authenticator = new SucceedingAirbnbEmailAuthenticatorStub(repository, unitOfWork);
        var handler = new ConnectAirbnbEmailMailboxCommandHandler(authenticator, repository, unitOfWork);

        var act = async () => await handler.Handle(new ConnectAirbnbEmailMailboxCommand(tenantId, Guid.NewGuid()), CancellationToken.None);

        var outcome = await act.Should().NotThrowAsync(
            "the handler's own post-auth read must open its own transaction, never nest inside the authenticator's already-closed one");
        outcome.Subject.IsSuccess.Should().BeTrue();
        outcome.Subject.Value.TenantId.Should().Be(tenantId);
        outcome.Subject.Value.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Connected);
    }

    /// <summary>Companion regression test for Disconnect - same nesting/RLS concern, opposite direction (clearing an existing connection).</summary>
    [Fact]
    public async Task DisconnectAirbnbEmailMailboxCommandHandler_reads_and_clears_the_connection_under_real_RLS_without_nesting()
    {
        var (tenantId, connectionId) = await SeedMailboxConnectionAsync();

        var seedTenantContext = new TenantContext();
        seedTenantContext.SetTenant(tenantId);
        await using (var seedDbContext = CreateDbContext(_migratorConnectionString, seedTenantContext))
        await using (var seedTransaction = await seedDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(seedDbContext, tenantId);
            var connection = await seedDbContext.AirbnbEmailMailboxConnections.SingleAsync(c => c.Id == connectionId);
            connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", DateTimeOffset.UtcNow);
            await seedDbContext.SaveChangesAsync();
            await seedTransaction.CommitAsync();
        }

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var repository = new AirbnbEmailMailboxConnectionRepository(dbContext);
        var unitOfWork = new AirbnbEmailUnitOfWork(dbContext, tenantContext);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();
        var protector = new AesGcmTokenCacheProtector(configuration);
        var tokenCacheStore = new PostgresAirbnbEmailTokenCacheStore(dbContext, unitOfWork, protector, TimeProvider.System);
        var handler = new DisconnectAirbnbEmailMailboxCommandHandler(repository, tokenCacheStore, unitOfWork);

        var act = async () => await handler.Handle(new DisconnectAirbnbEmailMailboxCommand(tenantId, Guid.NewGuid()), CancellationToken.None);

        var outcome = await act.Should().NotThrowAsync(
            "the handler's own pre-clear read must open its own transaction, never nest inside another one");
        outcome.Subject.IsSuccess.Should().BeTrue();
        outcome.Subject.Value.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Disconnected);
    }

    /// <summary>
    /// Airbnb Email Bridge Minimal Operations/UX gate: proves the
    /// processing-summary count aggregation is genuinely tenant-scoped under
    /// real RLS - a pure mocked-repository test is not enough proof for a
    /// cross-tenant aggregate concern (ChatGPT's own item 13 requirement for
    /// this gate).
    /// </summary>
    [Fact]
    public async Task CountByProcessingStatusForCurrentTenantAsync_never_counts_another_tenants_receipts()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        async Task SeedReceiptAsync(Guid tenantId, Action<AirbnbEmailMessageReceipt> mutate)
        {
            var tenantContext = new TenantContext();
            tenantContext.SetTenant(tenantId);
            await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await SetTenantAsync(dbContext, tenantId);

            var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), tenantId, Guid.NewGuid().ToString(), null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            mutate(receipt);
            dbContext.AirbnbEmailMessageReceipts.Add(receipt);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Tenant A: 2 NeedsReview, 1 Failed.
        await SeedReceiptAsync(tenantAId, r => r.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", DateTimeOffset.UtcNow));
        await SeedReceiptAsync(tenantAId, r => r.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", DateTimeOffset.UtcNow));
        await SeedReceiptAsync(tenantAId, r => r.MarkFailed("PARSER_EXCEPTION", "parser-v1", DateTimeOffset.UtcNow));

        // Tenant B: 1 Processed only - must never leak into Tenant A's counts or vice versa.
        await SeedReceiptAsync(tenantBId, r => r.MarkProcessed("RESERVATION_REMINDER", "HMZZZZZZZZ", "parser-v1", DateTimeOffset.UtcNow));

        var tenantAContext = new TenantContext();
        tenantAContext.SetTenant(tenantAId);
        await using var tenantADbContext = CreateDbContext(_appConnectionString, tenantAContext);
        var tenantARepository = new AirbnbEmailMessageReceiptRepository(tenantADbContext);
        var tenantAUnitOfWork = new AirbnbEmailUnitOfWork(tenantADbContext, tenantAContext);
        var tenantACounts = await tenantAUnitOfWork.ExecuteAsync(
            () => tenantARepository.CountByProcessingStatusForCurrentTenantAsync(CancellationToken.None), CancellationToken.None);

        tenantACounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.NeedsReview).Should().Be(2);
        tenantACounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.Failed).Should().Be(1);
        tenantACounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.Processed).Should().Be(0,
            "Tenant B's Processed receipt must never appear in Tenant A's counts");

        var tenantBContext = new TenantContext();
        tenantBContext.SetTenant(tenantBId);
        await using var tenantBDbContext = CreateDbContext(_appConnectionString, tenantBContext);
        var tenantBRepository = new AirbnbEmailMessageReceiptRepository(tenantBDbContext);
        var tenantBUnitOfWork = new AirbnbEmailUnitOfWork(tenantBDbContext, tenantBContext);
        var tenantBCounts = await tenantBUnitOfWork.ExecuteAsync(
            () => tenantBRepository.CountByProcessingStatusForCurrentTenantAsync(CancellationToken.None), CancellationToken.None);

        tenantBCounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.Processed).Should().Be(1);
        tenantBCounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.NeedsReview).Should().Be(0,
            "Tenant A's NeedsReview receipts must never appear in Tenant B's counts");
        tenantBCounts.GetValueOrDefault(AirbnbEmailMessageProcessingStatus.Failed).Should().Be(0);
    }

    // ---- Web OAuth architecture gate — AirbnbEmailOAuthTransaction (pre-authentication security state) ----

    [Fact]
    public async Task Migration_creates_the_airbnb_email_oauth_transactions_table()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'external_integrations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain("airbnb_email_oauth_transactions");
    }

    [Fact]
    public async Task Row_Level_Security_is_NOT_enabled_on_airbnb_email_oauth_transactions()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'airbnb_email_oauth_transactions' AND relnamespace = 'external_integrations'::regnamespace";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeFalse(
            "the tenant is not yet known at oauth/callback time — discovering it IS the point of this table, so RLS keyed on app.tenant_id would make every row unreadable exactly when it needs to be read (Web OAuth architecture gate, item 13)");
        reader.GetBoolean(1).Should().BeFalse();
    }

    [Fact]
    public async Task ConsumeByStateHashAsync_returns_the_trusted_correlation_data_and_marks_the_row_consumed()
    {
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());
        var repository = new AirbnbEmailOAuthTransactionRepository(dbContext);
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        repository.CreatePending(Guid.NewGuid(), tenantId, actorUserId, "hash-happy-path", [9, 9, 9], now, now.AddMinutes(10));
        await dbContext.SaveChangesAsync();

        var result = await repository.ConsumeByStateHashAsync("hash-happy-path", now.AddMinutes(1), CancellationToken.None);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(tenantId);
        result.ActorUserId.Should().Be(actorUserId);
        result.ProtectedPkceVerifier.Should().Equal(9, 9, 9);
    }

    [Fact]
    public async Task ConsumeByStateHashAsync_returns_null_for_an_unknown_state_hash()
    {
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());
        var repository = new AirbnbEmailOAuthTransactionRepository(dbContext);

        var result = await repository.ConsumeByStateHashAsync("never-created", DateTimeOffset.UtcNow, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ConsumeByStateHashAsync_returns_null_for_an_expired_transaction()
    {
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());
        var repository = new AirbnbEmailOAuthTransactionRepository(dbContext);
        var now = DateTimeOffset.UtcNow;

        repository.CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash-expired", [1], now.AddMinutes(-20), now.AddMinutes(-10));
        await dbContext.SaveChangesAsync();

        var result = await repository.ConsumeByStateHashAsync("hash-expired", now, CancellationToken.None);

        result.Should().BeNull("an expired transaction must never be consumable, even though the row still exists");
    }

    /// <summary>
    /// Proves single-use: the second call with the same state hash — even
    /// well within the expiry window — must fail, never silently succeed
    /// again (Web OAuth architecture gate, item 16).
    /// </summary>
    [Fact]
    public async Task ConsumeByStateHashAsync_returns_null_when_called_a_second_time()
    {
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());
        var repository = new AirbnbEmailOAuthTransactionRepository(dbContext);
        var now = DateTimeOffset.UtcNow;

        repository.CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash-single-use", [1], now, now.AddMinutes(10));
        await dbContext.SaveChangesAsync();

        var first = await repository.ConsumeByStateHashAsync("hash-single-use", now, CancellationToken.None);
        var second = await repository.ConsumeByStateHashAsync("hash-single-use", now, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull("a replayed callback presenting an already-consumed state must never succeed a second time");
    }

    /// <summary>
    /// The central atomicity proof ChatGPT's own architecture gate demanded
    /// (item 15-16/9): two callbacks racing to consume the SAME state must
    /// leave AT MOST ONE successful — never both, which a naive
    /// SELECT-then-UPDATE would allow.
    /// </summary>
    [Fact]
    public async Task ConsumeByStateHashAsync_lets_at_most_one_of_two_concurrent_callers_succeed()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var seedDbContext = CreateDbContext(_appConnectionString, new TenantContext()))
        {
            new AirbnbEmailOAuthTransactionRepository(seedDbContext)
                .CreatePending(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "hash-race", [1], now, now.AddMinutes(10));
            await seedDbContext.SaveChangesAsync();
        }

        await using var dbContextA = CreateDbContext(_appConnectionString, new TenantContext());
        await using var dbContextB = CreateDbContext(_appConnectionString, new TenantContext());
        var repositoryA = new AirbnbEmailOAuthTransactionRepository(dbContextA);
        var repositoryB = new AirbnbEmailOAuthTransactionRepository(dbContextB);

        var taskA = repositoryA.ConsumeByStateHashAsync("hash-race", now, CancellationToken.None);
        var taskB = repositoryB.ConsumeByStateHashAsync("hash-race", now, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r is not null).Should().Be(1, "concurrent callbacks presenting the same state must leave exactly one successful consumption, never zero or two");
    }

    /// <summary>
    /// The whole reason this table has no RLS/Global Query Filter (Web OAuth
    /// architecture gate's circularity correction): this call must succeed
    /// with NO tenant context ever set on this DbContext — proving the
    /// callback bootstrap sequence (consume state BEFORE SetTenant) is
    /// actually possible against the real schema, not just in the design.
    /// </summary>
    [Fact]
    public async Task ConsumeByStateHashAsync_works_with_no_ambient_tenant_context_at_all()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var seedDbContext = CreateDbContext(_appConnectionString, new TenantContext()))
        {
            new AirbnbEmailOAuthTransactionRepository(seedDbContext)
                .CreatePending(Guid.NewGuid(), tenantId, Guid.NewGuid(), "hash-no-tenant-context", [1], now, now.AddMinutes(10));
            await seedDbContext.SaveChangesAsync();
        }

        // Deliberately an unresolved TenantContext (IsResolved == false) —
        // unlike every other repository call in this Bounded Context, this
        // must NOT throw TenantContextNotResolvedException and must NOT
        // require IAirbnbEmailUnitOfWork at all.
        await using var dbContext = CreateDbContext(_appConnectionString, new TenantContext());
        var repository = new AirbnbEmailOAuthTransactionRepository(dbContext);

        var result = await repository.ConsumeByStateHashAsync("hash-no-tenant-context", now, CancellationToken.None);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(tenantId, "the tenant is RECOVERED from this call, not required as a precondition of it");
    }

    private async Task<(Guid TenantId, Guid ConnectionId)> SeedMailboxConnectionAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
        dbContext.AirbnbEmailMailboxConnections.Add(connection);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, connection.Id);
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid IntegrationId)> SeedIntegrationAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
        dbContext.WhatsAppIntegrations.Add(integration);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, integration.Id);
    }

    private static async Task SetTenantAsync(ExternalIntegrationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ExternalIntegrationsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options;

        return new ExternalIntegrationsDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
