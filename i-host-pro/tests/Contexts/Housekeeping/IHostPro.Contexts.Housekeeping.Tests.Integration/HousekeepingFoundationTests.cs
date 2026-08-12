using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Exercises the Housekeeping Bounded Context's physical foundation against
/// a real PostgreSQL instance (Testcontainers): migration application/
/// idempotency, Row-Level Security (fail-closed) on all four tenant-owned
/// tables, the application role's grants (including the least-privilege
/// asymmetry — no DELETE anywhere, no UPDATE on the append-only audit log),
/// the application role's lack of DDL/BYPASSRLS privileges, and the
/// messaging schema's provisioning — mirrors
/// <c>ReservationsFoundationTests</c> exactly (Fase 6, Checkpoint 3).
/// </summary>
public class HousekeepingFoundationTests : IClassFixture<HousekeepingFoundationTests.Fixture>
{
    private const string MessagingSchema = "housekeeping_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public HousekeepingFoundationTests(Fixture fixture)
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
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(HousekeepingDbContext));
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
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'housekeeping'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain(["cleanings", "cleaning_audit_log", "property_projection", "reservation_projection"]);
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
    public async Task Correct_tenant_sees_its_own_cleaning()
    {
        var (tenantId, cleaningId) = await SeedCleaningAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var cleanings = await dbContext.Cleanings.ToListAsync();

        cleanings.Should().ContainSingle(c => c.Id == cleaningId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_and_cannot_alter_them()
    {
        var (_, cleaningId) = await SeedCleaningAsync();
        var (unrelatedTenantId, _) = await SeedCleaningAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.Cleanings.Where(c => c.Id == cleaningId).ToListAsync();
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
            updateCommand.CommandText = "UPDATE housekeeping.cleanings SET status = 'Cancelled' WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", cleaningId);
            var affected = await updateCommand.ExecuteNonQueryAsync();

            affected.Should().Be(0);
        }
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedCleaningAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM housekeeping.cleanings"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var cleaning = Cleaning.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.Cleanings.Add(cleaning);

        // app.tenant_id was never set on this transaction.
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ---- Application role privileges ----

    [Fact]
    public async Task App_role_cannot_create_alter_or_drop_tables()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var createAct = async () => await ExecuteAsync(connection, "CREATE TABLE housekeeping.hack (id uuid PRIMARY KEY)");
        await createAct.Should().ThrowAsync<PostgresException>();

        var alterAct = async () => await ExecuteAsync(connection, "ALTER TABLE housekeeping.cleanings ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE housekeeping.cleanings");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_a_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, "ALTER TABLE housekeeping.cleanings DISABLE ROW LEVEL SECURITY");

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

    [Theory]
    [InlineData("cleanings")]
    [InlineData("cleaning_audit_log")]
    [InlineData("property_projection")]
    [InlineData("reservation_projection")]
    public async Task ENABLE_and_FORCE_row_level_security_are_active_on_every_tenant_owned_table(string tableName)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = @table AND relnamespace = 'housekeeping'::regnamespace";
        command.Parameters.AddWithValue("table", tableName);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    // ---- Least-privilege grants ----

    [Fact]
    public async Task App_role_cannot_delete_a_cleaning_row()
    {
        var (tenantId, cleaningId) = await SeedCleaningAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(connection, $"DELETE FROM housekeeping.cleanings WHERE id = '{cleaningId}'");

        await act.Should().ThrowAsync<PostgresException>();
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
            connection, $"UPDATE housekeeping.cleaning_audit_log SET action_code = 'tampered' WHERE id = '{entryId}'");

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
            connection, $"DELETE FROM housekeeping.cleaning_audit_log WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_accepts_insert_and_update_but_not_delete_on_the_property_projection()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");
            await ExecuteAsync(
                connection,
                $"INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active) VALUES ('{tenantId}', '{propertyId}', false)");
            await ExecuteAsync(
                connection,
                $"UPDATE housekeeping.property_projection SET is_active = true WHERE tenant_id = '{tenantId}' AND property_id = '{propertyId}'");
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");
            var act = async () => await ExecuteAsync(
                connection, $"DELETE FROM housekeeping.property_projection WHERE tenant_id = '{tenantId}' AND property_id = '{propertyId}'");

            await act.Should().ThrowAsync<PostgresException>();
        }
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

    private async Task<(Guid TenantId, Guid CleaningId)> SeedCleaningAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.Cleanings.Add(cleaning);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, cleaning.Id);
    }

    private async Task<(Guid TenantId, Guid EntryId)> SeedAuditEntryAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = CleaningAuditEntry.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Cleaning", Guid.NewGuid(), "cleaning_created",
            [], DateTimeOffset.UtcNow);
        dbContext.CleaningAuditLog.Add(entry);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, entry.Id);
    }

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
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
