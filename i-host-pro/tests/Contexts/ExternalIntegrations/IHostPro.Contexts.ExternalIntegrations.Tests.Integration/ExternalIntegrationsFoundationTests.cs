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
