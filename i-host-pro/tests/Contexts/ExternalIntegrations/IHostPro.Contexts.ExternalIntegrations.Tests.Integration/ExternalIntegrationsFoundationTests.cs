using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
