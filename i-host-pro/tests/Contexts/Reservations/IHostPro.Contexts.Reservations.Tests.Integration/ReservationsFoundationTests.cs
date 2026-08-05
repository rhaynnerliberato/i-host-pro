using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// Exercises the Reservations Bounded Context's physical foundation against
/// a real PostgreSQL instance (Testcontainers): migration application/
/// idempotency, Row-Level Security (fail-closed), the application role's
/// lack of DDL/BYPASSRLS privileges, the audit log's append-only grant, and
/// the messaging schema's provisioning — mirrors
/// <c>PropertyManagementFoundationTests</c> exactly (Fase 3 §4, closing the
/// RLS test gap identified in the continuity audit).
/// </summary>
public class ReservationsFoundationTests : IClassFixture<ReservationsFoundationTests.Fixture>
{
    private const string MessagingSchema = "reservations_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ReservationsFoundationTests(Fixture fixture)
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
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(ReservationsDbContext));
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
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'reservations'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain(["reservations", "reservation_audit_log"]);
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
    public async Task Correct_tenant_sees_its_own_reservation()
    {
        var (tenantId, reservationId) = await SeedReservationAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservations = await dbContext.Reservations.ToListAsync();

        reservations.Should().ContainSingle(r => r.Id == reservationId);
    }

    [Fact]
    public async Task Different_tenant_sees_zero_rows_and_cannot_alter_them()
    {
        var (_, reservationId) = await SeedReservationAsync();
        var (unrelatedTenantId, _) = await SeedReservationAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.Reservations.Where(r => r.Id == reservationId).ToListAsync();
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
            updateCommand.CommandText = "UPDATE reservations.reservations SET guest_name = 'tampered' WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", reservationId);
            var affected = await updateCommand.ExecuteNonQueryAsync();

            affected.Should().Be(0);
        }
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedReservationAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM reservations.reservations"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var reservation = Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test Guest", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 2, DateTimeOffset.UtcNow);
        dbContext.Reservations.Add(reservation);

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

        var createAct = async () => await ExecuteAsync(connection, "CREATE TABLE reservations.hack (id uuid PRIMARY KEY)");
        await createAct.Should().ThrowAsync<PostgresException>();

        var alterAct = async () => await ExecuteAsync(connection, "ALTER TABLE reservations.reservations ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE reservations.reservations");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_a_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, "ALTER TABLE reservations.reservations DISABLE ROW LEVEL SECURITY");

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

    [Fact]
    public async Task ENABLE_and_FORCE_row_level_security_are_active_on_the_reservations_table()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname = 'reservations' AND relnamespace = 'reservations'::regnamespace";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY must be active");
        reader.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY must be active — applies even to the table owner");
    }

    // ---- Audit log: append-only ----

    [Fact]
    public async Task Audit_log_accepts_insert_through_the_app_role()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = ReservationAuditEntry.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Reservation", Guid.NewGuid(), "reservation_created",
            [], DateTimeOffset.UtcNow);
        dbContext.ReservationAuditLog.Add(entry);

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
            connection, $"UPDATE reservations.reservation_audit_log SET action_code = 'tampered' WHERE id = '{entryId}'");

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
            connection, $"DELETE FROM reservations.reservation_audit_log WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
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

    private async Task<(Guid TenantId, Guid ReservationId)> SeedReservationAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Test Guest", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 2, DateTimeOffset.UtcNow);
        dbContext.Reservations.Add(reservation);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, reservation.Id);
    }

    private async Task<(Guid TenantId, Guid EntryId)> SeedAuditEntryAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = ReservationAuditEntry.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Reservation", Guid.NewGuid(), "reservation_created",
            [], DateTimeOffset.UtcNow);
        dbContext.ReservationAuditLog.Add(entry);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, entry.Id);
    }

    private static async Task SetTenantAsync(ReservationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ReservationsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;

        return new ReservationsDbContext(options, tenantContext);
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
