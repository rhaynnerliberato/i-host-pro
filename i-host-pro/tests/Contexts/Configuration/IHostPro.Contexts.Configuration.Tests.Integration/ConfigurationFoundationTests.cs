using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// Exercises the Configuration &amp; Policy Bounded Context's physical
/// foundation against a real PostgreSQL instance (Testcontainers):
/// migration application/idempotency, the seeded policy catalog, Row-Level
/// Security (fail-closed) on the two tenant-owned tables, the absence of RLS
/// on the two platform/global tables, the application role's lack of DDL/
/// BYPASSRLS privileges, the append-only audit log grant, the column-
/// restricted update grant on <c>policy_values</c>, the partial unique index
/// enforcing a single current version per scope, and the messaging schema's
/// provisioning — mirrors <c>ReservationsFoundationTests</c> exactly (Fase 5,
/// Incremento 1, Checkpoint 2).
/// </summary>
public class ConfigurationFoundationTests : IClassFixture<ConfigurationFoundationTests.Fixture>
{
    private const string MessagingSchema = "configuration_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ConfigurationFoundationTests(Fixture fixture)
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

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionMessagingSchemaAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionMessagingSchemaAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(ConfigurationDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Migration ----

    [Fact]
    public async Task Migration_applies_cleanly_and_creates_the_expected_tables()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'configuration'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain(["policy_definitions", "policy_values", "policy_audit_log", "global_policy_values"]);
    }

    [Fact]
    public async Task Migration_is_idempotent_on_a_second_run()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var act = async () => await dbContext.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Seed catalog (official decision 2.2: definitions only, no default values) ----

    [Fact]
    public async Task Policy_definitions_are_seeded_with_the_two_catalog_entries()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var codes = await dbContext.PolicyDefinitions.Select(d => d.Id).ToListAsync();

        codes.Should().BeEquivalentTo(["EARLY_CHECKIN", "LATE_CHECKOUT"]);
    }

    [Fact]
    public async Task No_policy_values_or_global_policy_values_are_seeded()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var policyValueCount = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM configuration.policy_values"))!;
        var globalValueCount = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM configuration.global_policy_values"))!;

        policyValueCount.Should().Be(0, "official decision 2.2: no default values are invented for any policy");
        globalValueCount.Should().Be(0, "GLOBAL remains read-only and empty until a default is explicitly approved");
    }

    // ---- Row-Level Security (fail-closed) on the two tenant-owned tables ----

    [Fact]
    public async Task Correct_tenant_sees_its_own_policy_value()
    {
        var (tenantId, policyValueId) = await SeedPolicyValueAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var values = await dbContext.PolicyValues.ToListAsync();

        values.Should().ContainSingle(v => v.Id == policyValueId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_and_cannot_alter_them()
    {
        var (_, policyValueId) = await SeedPolicyValueAsync();
        var (unrelatedTenantId, _) = await SeedPolicyValueAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.PolicyValues.Where(v => v.Id == policyValueId).ToListAsync();
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
            updateCommand.CommandText = "UPDATE configuration.policy_values SET is_current = false WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", policyValueId);
            var affected = await updateCommand.ExecuteNonQueryAsync();

            affected.Should().Be(0);
        }
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedPolicyValueAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM configuration.policy_values"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), Guid.NewGuid(), "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");
        dbContext.PolicyValues.Add(value);

        // app.tenant_id was never set on this transaction.
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("policy_values")]
    [InlineData("policy_audit_log")]
    public async Task ENABLE_and_FORCE_row_level_security_are_active_on_tenant_owned_tables(string tableName)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = @tableName AND relnamespace = 'configuration'::regnamespace";
        command.Parameters.AddWithValue("tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    // ---- No Row-Level Security on the two platform/global tables ----

    [Theory]
    [InlineData("policy_definitions")]
    [InlineData("global_policy_values")]
    public async Task Platform_catalog_tables_carry_no_row_level_security(string tableName)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = @tableName AND relnamespace = 'configuration'::regnamespace";
        command.Parameters.AddWithValue("tableName", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeFalse($"{tableName} is a platform catalog table, never tenant-owned");
        reader.GetBoolean(1).Should().BeFalse($"{tableName} is a platform catalog table, never tenant-owned");
    }

    // ---- Application role privileges ----

    [Fact]
    public async Task App_role_cannot_create_alter_or_drop_tables()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var createAct = async () => await ExecuteAsync(connection, "CREATE TABLE configuration.hack (id uuid PRIMARY KEY)");
        await createAct.Should().ThrowAsync<PostgresException>();

        var alterAct = async () => await ExecuteAsync(connection, "ALTER TABLE configuration.policy_values ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE configuration.policy_values");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_a_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, "ALTER TABLE configuration.policy_values DISABLE ROW LEVEL SECURITY");

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

    // ---- policy_values: append-only, column-restricted update ----

    [Fact]
    public async Task App_role_can_flip_is_current_on_a_policy_value()
    {
        var (tenantId, policyValueId) = await SeedPolicyValueAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId:D}'");

        var act = async () => await ExecuteAsync(
            connection, $"UPDATE configuration.policy_values SET is_current = false WHERE id = '{policyValueId:D}'");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task App_role_cannot_update_the_value_column_of_a_policy_value()
    {
        var (tenantId, policyValueId) = await SeedPolicyValueAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId:D}'");

        var act = async () => await ExecuteAsync(
            connection, $"UPDATE configuration.policy_values SET value = '{{}}' WHERE id = '{policyValueId:D}'");

        await act.Should().ThrowAsync<PostgresException>(
            "official decisions §4: a version's value must never be overwritten — only is_current may change");
    }

    [Fact]
    public async Task App_role_cannot_delete_a_policy_value()
    {
        var (tenantId, policyValueId) = await SeedPolicyValueAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId:D}'");

        var act = async () => await ExecuteAsync(
            connection, $"DELETE FROM configuration.policy_values WHERE id = '{policyValueId:D}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Only_one_current_row_is_allowed_per_scope()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var first = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");
        dbContext.PolicyValues.Add(first);
        await dbContext.SaveChangesAsync();

        // A second row for the very same scope, also marked current, without
        // superseding the first — the partial unique index must reject it.
        var second = PolicyValue.CreateNextVersion(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), PolicyVersion.First(),
            """{"allowed":false}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "conflicting change");
        dbContext.PolicyValues.Add(second);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ---- policy_audit_log: append-only ----

    [Fact]
    public async Task Audit_log_accepts_insert_through_the_app_role()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = PolicyAuditEntry.Create(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, "initial setup", "Api");
        dbContext.PolicyAuditLog.Add(entry);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task App_role_cannot_update_audit_log_rows()
    {
        var (tenantId, entryId) = await SeedAuditEntryAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(
            connection, $"UPDATE configuration.policy_audit_log SET reason = 'tampered' WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_delete_audit_log_rows()
    {
        var (tenantId, entryId) = await SeedAuditEntryAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(
            connection, $"DELETE FROM configuration.policy_audit_log WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Platform catalog: readable, never writable by the app role ----

    [Fact]
    public async Task App_role_can_read_policy_definitions_but_cannot_write_to_them()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM configuration.policy_definitions"))!;
        count.Should().Be(2);

        var insertAct = async () => await ExecuteAsync(
            connection,
            "INSERT INTO configuration.policy_definitions (code, name, description, category, value_type, schema_version, is_active) " +
            "VALUES ('HACK', 'Hack', 'Hack', 'Hack', 'Object', 1, true)");
        await insertAct.Should().ThrowAsync<PostgresException>();

        var updateAct = async () => await ExecuteAsync(
            connection, "UPDATE configuration.policy_definitions SET is_active = false WHERE code = 'EARLY_CHECKIN'");
        await updateAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_can_read_global_policy_values_but_cannot_write_to_them()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var countAct = async () => await ExecuteScalarAsync(connection, "SELECT count(*) FROM configuration.global_policy_values");
        await countAct.Should().NotThrowAsync();

        var insertAct = async () => await ExecuteAsync(
            connection,
            $"INSERT INTO configuration.global_policy_values (id, policy_code, value, created_at_utc) " +
            $"VALUES ('{Guid.NewGuid():D}', 'EARLY_CHECKIN', '{{}}', now())");
        await insertAct.Should().ThrowAsync<PostgresException>();
    }

    // ---- Messaging schema provisioning ----

    [Fact]
    public async Task Messaging_schema_is_provisioned_and_readable_by_the_app_role()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var tableCount = (long)(await ExecuteScalarAsync(
            connection,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{MessagingSchema}'"))!;

        tableCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task App_role_has_no_ddl_privileges_on_the_messaging_schema()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, $"CREATE TABLE {MessagingSchema}.hack (id uuid PRIMARY KEY)");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid PolicyValueId)> SeedPolicyValueAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");
        dbContext.PolicyValues.Add(value);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, value.Id);
    }

    private async Task<(Guid TenantId, Guid EntryId)> SeedAuditEntryAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = PolicyAuditEntry.Create(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            null, 1, null, """{"allowed":true}""", Guid.NewGuid(), DateTimeOffset.UtcNow, "initial setup", "Api");
        dbContext.PolicyAuditLog.Add(entry);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, entry.Id);
    }

    private static async Task SetTenantAsync(ConfigurationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ConfigurationDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;

        return new ConfigurationDbContext(options, tenantContext);
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
