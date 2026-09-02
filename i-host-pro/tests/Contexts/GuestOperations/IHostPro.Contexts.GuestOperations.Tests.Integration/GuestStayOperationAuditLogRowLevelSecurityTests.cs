using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.GuestOperations.Tests.Integration;

/// <summary>
/// Exercises <c>guest_operations.guest_stay_operation_audit_log</c> against a
/// real PostgreSQL instance (Fase 12, Checkpoint 4 — Guest Access Durable
/// Audit Decision Gate): Row-Level Security, append-only grants, and that no
/// sensitive guest-facing content is ever persisted. Mirrors
/// <c>Identity.Tests.Integration.SecurityAuditLogRowLevelSecurityTests</c>'
/// own structure — simplified (no tenant seeding needed): unlike
/// <c>security_audit_log</c>, this table has no foreign key to
/// <c>identity.tenants</c> (matches <c>reservation_audit_log</c>'s own
/// established precedent — an audit table outside the schema that owns
/// <c>tenants</c> never takes a cross-schema FK on tenant_id in this
/// codebase).
/// </summary>
public class GuestStayOperationAuditLogRowLevelSecurityTests : IClassFixture<GuestStayOperationAuditLogRowLevelSecurityTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public GuestStayOperationAuditLogRowLevelSecurityTests(Fixture fixture)
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

    [Fact]
    public async Task Migration_creates_the_audit_log_with_row_level_security_enabled_and_forced()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var (rowSecurity, forceRowSecurity) = await GetRowSecurityFlagsAsync(connection, "guest_stay_operation_audit_log");
        rowSecurity.Should().BeTrue();
        forceRowSecurity.Should().BeTrue();

        var policyCount = (long)(await ExecuteScalarAsync(connection, """
            SELECT count(*) FROM pg_policies
            WHERE schemaname = 'guest_operations' AND tablename = 'guest_stay_operation_audit_log' AND policyname = 'tenant_isolation'
            """))!;
        policyCount.Should().Be(1);
    }

    [Fact]
    public async Task Ihostpro_app_has_insert_and_select_but_never_update_or_delete()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var grantedPrivileges = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT privilege_type FROM information_schema.role_table_grants
                WHERE table_schema = 'guest_operations' AND table_name = 'guest_stay_operation_audit_log' AND grantee = 'ihostpro_app'
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                grantedPrivileges.Add(reader.GetString(0));
        }

        grantedPrivileges.Should().Contain("INSERT");
        grantedPrivileges.Should().Contain("SELECT");
        grantedPrivileges.Should().NotContain("UPDATE", "an audit trail that could be mutated after the fact would not be a trail");
        grantedPrivileges.Should().NotContain("DELETE", "an audit trail that could be erased after the fact would not be a trail");
    }

    [Fact]
    public async Task An_actor_and_target_entry_persists_and_reads_back_correctly()
    {
        var tenantId = Guid.NewGuid();
        var guestStayOperationId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var entryId = await InsertAuditEntryAsync(
            tenantId, guestStayOperationId, GuestStayOperationAuditAction.AccessDeliveryRequested, "User", actorId);

        await using var dbContext = CreateAppDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.GuestStayOperationAuditLog.SingleAsync(e => e.Id == entryId);

        entry.GuestStayOperationId.Should().Be(guestStayOperationId);
        entry.Action.Should().Be(GuestStayOperationAuditAction.AccessDeliveryRequested);
        entry.ActorType.Should().Be("User");
        entry.ActorId.Should().Be(actorId);
    }

    [Fact]
    public async Task Correct_tenant_sees_its_own_audit_entry()
    {
        var tenantId = Guid.NewGuid();
        var entryId = await InsertAuditEntryAsync(
            tenantId, Guid.NewGuid(), GuestStayOperationAuditAction.CheckedIn, "User", Guid.NewGuid());

        await using var dbContext = CreateAppDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entries = await dbContext.GuestStayOperationAuditLog.ToListAsync();

        entries.Should().ContainSingle(e => e.Id == entryId);
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows_for_another_tenants_audit_entry()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var entryId = await InsertAuditEntryAsync(
            tenantAId, Guid.NewGuid(), GuestStayOperationAuditAction.CheckedOut, "User", Guid.NewGuid());

        await using var dbContext = CreateAppDbContextWithTenant(tenantBId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantBId);

        var visible = await dbContext.GuestStayOperationAuditLog.Where(e => e.Id == entryId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_with_mismatched_tenant_id_is_rejected_by_WITH_CHECK()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(connection, $"""
            INSERT INTO guest_operations.guest_stay_operation_audit_log
                (id, tenant_id, guest_stay_operation_id, action, actor_type, actor_id, occurred_at_utc)
            VALUES ('{Guid.NewGuid()}', '{otherTenantId}', '{Guid.NewGuid()}', 'CheckedIn', 'User', '{Guid.NewGuid()}', now());
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task No_guest_facing_content_column_exists_on_the_audit_table()
    {
        // Structural guarantee, not just a code-review discipline (mandate
        // §9): the table itself must never have grown a column that could
        // carry AccessCredential/QR/message content/GuestPhone/GuestEmail/
        // provider payload — only audit metadata.
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var columnNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT column_name FROM information_schema.columns
                WHERE table_schema = 'guest_operations' AND table_name = 'guest_stay_operation_audit_log'
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columnNames.Add(reader.GetString(0));
        }

        columnNames.Should().BeEquivalentTo(
            ["id", "tenant_id", "guest_stay_operation_id", "action", "actor_type", "actor_id", "occurred_at_utc"]);
    }

    private async Task<Guid> InsertAuditEntryAsync(
        Guid tenantId, Guid guestStayOperationId, GuestStayOperationAuditAction action, string actorType, Guid actorId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var id = Guid.NewGuid();
        dbContext.GuestStayOperationAuditLog.Add(GuestStayOperationAuditEntry.Record(
            id, tenantId, guestStayOperationId, action, actorType, actorId, DateTimeOffset.UtcNow));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return id;
    }

    private static async Task SetTenantAsync(GuestOperationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private GuestOperationsDbContext CreateAppDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_appConnectionString, tenantContext);
    }

    private GuestOperationsDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static GuestOperationsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;

        return new GuestOperationsDbContext(options, tenantContext);
    }

    private static async Task<(bool RowSecurity, bool ForceRowSecurity)> GetRowSecurityFlagsAsync(
        NpgsqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relrowsecurity, relforcerowsecurity FROM pg_class
            WHERE relnamespace = 'guest_operations'::regnamespace AND relname = @tableName
            """;
        command.Parameters.AddWithValue("tableName", tableName);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetBoolean(0), reader.GetBoolean(1));
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
