using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Exercises `identity.security_audit_log` against a real PostgreSQL
/// instance (Testcontainers): Row-Level Security, append-only guarantees, and
/// stable ASCII code persistence — Incremento 2 plan, ajuste 1-6, Etapa 5.
///
/// Container/role provisioning mirrors <see cref="IdentityRowLevelSecurityTests"/>
/// exactly (Incremento 1 plan) — kept as a separate file because this table's
/// concerns (append-only, no FK cascade to User/Session/RefreshToken, ASCII
/// code persistence) are distinct from the users/sessions/refresh_tokens
/// scenarios already covered there.
/// </summary>
public class SecurityAuditLogRowLevelSecurityTests : IClassFixture<SecurityAuditLogRowLevelSecurityTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public SecurityAuditLogRowLevelSecurityTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// </summary>
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
    public async Task Migration_creates_security_audit_log_with_row_level_security_enabled_and_forced()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var (rowSecurity, forceRowSecurity) = await GetRowSecurityFlagsAsync(connection, "security_audit_log");
        rowSecurity.Should().BeTrue();
        forceRowSecurity.Should().BeTrue();

        var policyCount = (long)(await ExecuteScalarAsync(connection, """
            SELECT count(*) FROM pg_policies
            WHERE schemaname = 'identity' AND tablename = 'security_audit_log' AND policyname = 'tenant_isolation'
            """))!;
        policyCount.Should().Be(1);
    }

    [Fact]
    public async Task Ihostpro_app_automatically_received_crud_grants_via_default_privileges()
    {
        // No explicit GRANT statement exists in the AddSecurityAuditLog
        // migration — this proves, rather than assumes, that InitialCreate's
        // ALTER DEFAULT PRIVILEGES already covers tables created afterwards
        // (Architecture Principles, Section 10).
        await using var dbContext = CreateAppDbContextWithTenant(Guid.NewGuid());

        var act = async () => await dbContext.SecurityAuditLog.ToListAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Event_type_and_reason_code_are_persisted_as_stable_ascii_text_not_free_text_or_integers()
    {
        var (tenantId, userId) = await SeedTenantWithUserAsync();
        await InsertAuditEntryAsync(
            tenantId, SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.SessionNotActive, userId: userId);

        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // FORCE ROW LEVEL SECURITY applies even to the owning ihostpro_migrator
        // role — without this, the query below would silently return zero
        // rows regardless of the WHERE clause, for the wrong reason.
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_type, reason_code FROM identity.security_audit_log WHERE tenant_id = @tenantId";
        command.Parameters.AddWithValue("tenantId", tenantId);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetString(0).Should().Be(nameof(SecurityAuditEventType.RefreshRejected));
        reader.GetString(1).Should().Be(nameof(SecurityAuditReasonCode.SessionNotActive));
    }

    [Fact]
    public async Task Correct_tenant_sees_its_own_audit_entry()
    {
        var (tenantId, userId) = await SeedTenantWithUserAsync();
        var entryId = await InsertAuditEntryAsync(tenantId, SecurityAuditEventType.LoginSucceeded, userId: userId);

        await using var dbContext = CreateAppDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entries = await dbContext.SecurityAuditLog.ToListAsync();

        entries.Should().ContainSingle(e => e.Id == entryId);
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows_for_another_tenants_audit_entry()
    {
        var (tenantAId, userAId) = await SeedTenantWithUserAsync();
        var entryId = await InsertAuditEntryAsync(tenantAId, SecurityAuditEventType.LoginSucceeded, userId: userAId);

        var (tenantBId, _) = await SeedTenantWithUserAsync();

        await using var dbContext = CreateAppDbContextWithTenant(tenantBId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantBId);

        var visible = await dbContext.SecurityAuditLog.Where(e => e.Id == entryId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        var (tenantId, userId) = await SeedTenantWithUserAsync();
        await InsertAuditEntryAsync(tenantId, SecurityAuditEventType.LoginSucceeded, userId: userId);

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM identity.security_audit_log"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        var (tenantId, userId) = await SeedTenantWithUserAsync();

        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        // app.tenant_id deliberately never set on this transaction.
        dbContext.SecurityAuditLog.Add(SecurityAuditEntry.Record(
            Guid.NewGuid(), tenantId, SecurityAuditEventType.LoginSucceeded, DateTimeOffset.UtcNow, Guid.NewGuid(),
            userId: userId));

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Insert_with_mismatched_tenant_id_is_rejected_by_WITH_CHECK()
    {
        var (tenantId, _) = await SeedTenantWithUserAsync();
        var otherTenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(connection, $"""
            INSERT INTO identity.security_audit_log (id, tenant_id, event_type, occurred_at, correlation_id)
            VALUES ('{Guid.NewGuid()}', '{otherTenantId}', 'LoginSucceeded', now(), '{Guid.NewGuid()}');
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Deleting_the_referenced_user_does_not_remove_or_orphan_the_audit_entry()
    {
        // user_id/session_id/refresh_token_id are deliberately not foreign
        // keys (Incremento 2 plan, ajuste 5) — this proves it empirically:
        // even a raw, direct DELETE of the referenced user must leave the
        // audit row completely untouched, still carrying the original
        // (now-dangling) user_id.
        var (tenantId, userId) = await SeedTenantWithUserAsync();
        var entryId = await InsertAuditEntryAsync(tenantId, SecurityAuditEventType.LoginSucceeded, userId: userId);

        await using (var connection = new NpgsqlConnection(_migratorConnectionString))
        {
            await connection.OpenAsync();
            // FORCE ROW LEVEL SECURITY applies even to the owning
            // ihostpro_migrator role — the tenant must still be set,
            // otherwise this DELETE would itself be silently scoped to zero
            // rows instead of exercising the actual "no FK" guarantee.
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");
            var deleted = await ExecuteNonQueryAsync(connection, $"DELETE FROM identity.users WHERE id = '{userId}'");
            deleted.Should().Be(1, "the delete itself must actually affect the seeded user, not be silently scoped away");
            await transaction.CommitAsync();
        }

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var verifyTransaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.SecurityAuditLog.SingleAsync(e => e.Id == entryId);

        entry.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task An_entry_with_every_optional_reference_left_null_persists_correctly()
    {
        // Covers the case a future rejection audit entry will need — e.g. an
        // unknown e-mail within a validly resolved tenant, where no user,
        // session or refresh token can be identified at all.
        var (tenantId, _) = await SeedTenantWithUserAsync();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entryId = Guid.NewGuid();
        dbContext.SecurityAuditLog.Add(SecurityAuditEntry.Record(
            entryId, tenantId, SecurityAuditEventType.LoginRejected, DateTimeOffset.UtcNow, Guid.NewGuid(),
            reasonCode: SecurityAuditReasonCode.UserNotFound));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(verifyDbContext, tenantId);

        var entry = await verifyDbContext.SecurityAuditLog.SingleAsync(e => e.Id == entryId);

        entry.UserId.Should().BeNull();
        entry.SessionId.Should().BeNull();
        entry.RefreshTokenId.Should().BeNull();
        entry.IpAddress.Should().BeNull();
        entry.ReasonCode.Should().Be(SecurityAuditReasonCode.UserNotFound);
    }

    private async Task<Guid> InsertAuditEntryAsync(
        Guid tenantId,
        SecurityAuditEventType eventType,
        SecurityAuditReasonCode? reasonCode = null,
        Guid? userId = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var id = Guid.NewGuid();
        dbContext.SecurityAuditLog.Add(SecurityAuditEntry.Record(
            id, tenantId, eventType, DateTimeOffset.UtcNow, Guid.NewGuid(), reasonCode: reasonCode, userId: userId));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return id;
    }

    private async Task<(Guid TenantId, Guid UserId)> SeedTenantWithUserAsync()
    {
        var tenantId = Guid.NewGuid();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        var user = User.Register(
            Guid.NewGuid(), tenant.Id, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User",
            PasswordHash.FromEncoded("placeholder"), DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenant.Id, user.Id);
    }

    private static async Task SetTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateAppDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_appConnectionString, tenantContext);
    }

    /// <summary>
    /// Sets the C# <see cref="ITenantContext"/> (consulted by EF's Global
    /// Query Filter) — a distinct mechanism from the Postgres
    /// <c>app.tenant_id</c> session variable set by <see cref="SetTenantAsync"/>
    /// (consulted by the RLS policy itself). Both must be set for a query
    /// through this DbContext to see anything at all; a context built with a
    /// tenant never set on the C# side returns zero rows from every LINQ
    /// query regardless of what RLS would otherwise allow — the exact defect
    /// found and fixed while writing these tests.
    /// </summary>
    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task<(bool RowSecurity, bool ForceRowSecurity)> GetRowSecurityFlagsAsync(
        NpgsqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relrowsecurity, relforcerowsecurity FROM pg_class
            WHERE relnamespace = 'identity'::regnamespace AND relname = @tableName
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

    private static async Task<int> ExecuteNonQueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
