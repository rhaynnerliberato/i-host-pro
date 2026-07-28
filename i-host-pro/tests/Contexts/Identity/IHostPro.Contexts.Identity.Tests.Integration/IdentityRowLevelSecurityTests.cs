using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Exercises the physical schema against a real PostgreSQL instance
/// (Testcontainers): migration application, Row-Level Security, and the
/// tenant-aware composite foreign keys — Incremento 1 plan, adendo final,
/// Sections 1, 3, 5, 8.
///
/// <para>
/// <b>Known limitation, disclosed per "ai-rules/05 - Testing and Validation
/// Policy.md" Section 25:</b> these tests could not be executed in this
/// development environment — the Docker daemon is unavailable here (see the
/// Incremento 1 report). They compile and are reviewed, but have not
/// produced a passing (or failing) run. They must be executed in a real CI
/// environment (e.g. GitHub Actions' Docker-enabled Ubuntu runners) before
/// this evidence can be considered validated.
/// </para>
/// </summary>
public class IdentityRowLevelSecurityTests : IClassFixture<IdentityRowLevelSecurityTests.Fixture>
{
    private readonly PostgreSqlContainer _container;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public IdentityRowLevelSecurityTests(Fixture fixture)
    {
        _container = fixture.Container;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method (Etapa 15A
    /// stabilization) — xUnit's <see cref="IAsyncLifetime"/> on the test
    /// class itself creates a fresh instance (and thus a fresh Testcontainer)
    /// per <c>[Fact]</c>; with 9 test files each spinning their own
    /// PostgreSQL/Redis/RabbitMQ containers, that meant 100+ container
    /// start/stop cycles in a single full suite run, which intermittently
    /// overwhelmed the Docker daemon's API (observed as a transient
    /// <c>Docker.DotNet</c>/<c>ChunkedReadStream</c> exception on an
    /// otherwise-unrelated test). <see cref="IClassFixture{TFixture}"/>
    /// shares one container per class instead. Test isolation is unaffected:
    /// every test already seeds its own randomly-keyed data (fresh
    /// <c>Guid.NewGuid()</c> tenant/user/etc. per test), never relies on a
    /// pristine database.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer Container { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
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

                // Mirrors docker/postgres/init/01-create-roles.sh's privilege
                // model exactly (Incremento 1 plan, adendo final, Section 7):
                // ihostpro_migrator needs CREATE on the database itself to run
                // the first-ever migration (CREATE SCHEMA identity) — found
                // during Incremento 1 homologation ("permission denied for
                // database"). No grant on `public` (obsolete: the migrations
                // history table lives in `identity`, not `public`, since the
                // matching fix in IdentityDbContextFactory /
                // IdentityModuleExtensions / MigrationRunner). ihostpro_app
                // receives no equivalent grant.
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

        public async Task DisposeAsync() => await Container.DisposeAsync();
    }

    [Fact]
    public async Task Migration_applies_cleanly_and_seeds_the_platform_catalog()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        (await dbContext.Roles.CountAsync()).Should().Be(7);
        (await dbContext.Permissions.CountAsync()).Should().Be(32);
        (await dbContext.RolePermissions.CountAsync()).Should().Be(39);
    }

    [Fact]
    public async Task Correct_tenant_sees_its_own_row()
    {
        var (tenantAId, userAId) = await SeedTenantWithUserAsync();

        // The C# ITenantContext (used by EF's Global Query Filter) and the
        // Postgres session variable (used by the RLS policy) must both
        // resolve to the same tenant — querying via a raw IdentityDbContext
        // (outside ITenantAwareUnitOfWork) satisfies neither automatically.
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantAId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantAId.ToString()}, true)");

        var users = await dbContext.Users.ToListAsync();

        users.Should().ContainSingle(u => u.Id == userAId);
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows()
    {
        var (tenantAId, userAId) = await SeedTenantWithUserAsync();

        // A genuinely provisioned, resolved second tenant — not an unused,
        // never-persisted Guid — so this test exercises "a different, valid
        // tenant cannot see tenant A's row", not merely "no tenant
        // resolved" (already covered by
        // Absent_tenant_context_sees_zero_rows_and_does_not_throw).
        var (unrelatedTenantId, _) = await SeedTenantWithUserAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {unrelatedTenantId.ToString()}, true)");

        // Filtered specifically to tenant A's user, rather than asserting
        // the whole result set is empty — tenant B legitimately sees its
        // own seeded user, which is correct and must not be mistaken for a
        // cross-tenant leak.
        var visibleRows = await dbContext.Users.Where(u => u.Id == userAId).ToListAsync();

        visibleRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        // Mirrors, deliberately, the exact bug found during Incremento 1
        // homologation (SeedTenantWithUserAsync originally omitted this
        // step) — asserted here as the expected, required fail-closed
        // behavior rather than left as an accidental defect.
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        var user = User.Register(
            Guid.NewGuid(), tenant.Id, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User",
            PasswordHash.FromEncoded("placeholder"), DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        // app.tenant_id was never set on this transaction.
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Wrong_tenant_cannot_update_the_row()
    {
        var (tenantAId, userId) = await SeedTenantWithUserAsync();

        // A genuinely provisioned, resolved second tenant — see
        // Wrong_tenant_sees_zero_rows for why this must not be an unused,
        // never-persisted Guid.
        var (unrelatedTenantId, _) = await SeedTenantWithUserAsync();

        await using (var connection = new NpgsqlConnection(_appConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var setConfigCommand = connection.CreateCommand())
            {
                setConfigCommand.CommandText = "SELECT set_config('app.tenant_id', @tenantId, true)";
                setConfigCommand.Parameters.AddWithValue("tenantId", unrelatedTenantId.ToString());
                await setConfigCommand.ExecuteScalarAsync();
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE identity.users SET full_name = 'Renamed' WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", userId);
            var affectedRows = await updateCommand.ExecuteNonQueryAsync();

            // RLS's USING clause makes the row invisible to this session, so
            // the UPDATE matches zero rows instead of erroring.
            affectedRows.Should().Be(0);

            await transaction.CommitAsync();
        }

        // Verified from the owning tenant's perspective: the original row
        // must remain unchanged.
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantAId);
        await using var verifyDbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
        await verifyDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantAId.ToString()}, true)");

        var user = await verifyDbContext.Users.SingleAsync(u => u.Id == userId);
        user.FullName.Should().Be("Test User");
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedTenantWithUserAsync();

        // No SET LOCAL app.tenant_id was ever issued on this connection —
        // current_setting(..., true) returns NULL, and `tenant_id = NULL` is
        // never true (fail-closed), never an error.
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM identity.users"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_with_mismatched_tenant_id_is_rejected_by_WITH_CHECK()
    {
        var (tenantAId, _) = await SeedTenantWithUserAsync();
        var otherTenantId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantAId);
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantAId}'");

        // Attempting to insert a row whose tenant_id does not match the
        // session's app.tenant_id must violate the WITH CHECK clause.
        var act = async () => await ExecuteAsync(connection, $"""
            INSERT INTO identity.users
                (id, tenant_id, email, normalized_email, full_name, password_hash, security_stamp, status, failed_access_count, created_at, updated_at)
            VALUES
                ('{Guid.NewGuid()}', '{otherTenantId}', 'x@y.com', 'x@y.com', 'X', 'hash', 'stamp', 1, 0, now(), now());
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Pooled_connection_reuse_does_not_leak_tenant_context_between_transactions()
    {
        var (tenantAId, _) = await SeedTenantWithUserAsync();
        var (tenantBId, _) = await SeedTenantWithUserAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        await using (var tx1 = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantAId}'");
            var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM identity.users"))!;
            count.Should().Be(1);
            await tx1.CommitAsync();
        }

        // Same physical connection, new transaction, different tenant, no
        // explicit reset needed — SET LOCAL from tx1 must not survive commit.
        await using (var tx2 = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantBId}'");
            var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM identity.users"))!;
            count.Should().Be(1); // only tenant B's row, not A's leaking through
            await tx2.CommitAsync();
        }
    }

    [Fact]
    public async Task App_role_cannot_bypass_row_level_security_via_row_security_off()
    {
        var (tenantAId, _) = await SeedTenantWithUserAsync();
        var unrelatedTenantId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{unrelatedTenantId}'");

        // The SET command itself may succeed, but its effect must not expose
        // another tenant's rows — either the subsequent query fails, or it
        // returns zero rows. It must never return tenant A's row.
        try
        {
            await ExecuteAsync(connection, "SET LOCAL row_security = off");
            var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM identity.users"))!;
            count.Should().Be(0);
        }
        catch (PostgresException)
        {
            // Also an acceptable, safe outcome: PostgreSQL refused the
            // operation entirely rather than exposing rows.
        }
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_a_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(connection, "ALTER TABLE identity.users DISABLE ROW LEVEL SECURITY");

        await act.Should().ThrowAsync<PostgresException>();
    }

    private async Task<(Guid TenantId, Guid UserId)> SeedTenantWithUserAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        try
        {
            // Reproduces explicitly, for this test helper, the same
            // semantics ITenantAwareUnitOfWork (BuildingBlocks.Infrastructure)
            // applies in production: the tenant session variable is set as
            // the first statement inside the transaction, before any
            // tenant-owned row is written. set_config() is a regular SQL
            // function — unlike a raw SET command, it accepts a bind
            // parameter, so this never interpolates the tenant id directly
            // into SQL text.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

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
        catch
        {
            // await using on `transaction` rolls back automatically via
            // DisposeAsync if CommitAsync was never reached; this catch only
            // exists to make that guarantee explicit for readers, matching
            // requirement 8 (rollback/dispose on failure).
            throw;
        }
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        // Kept identical to IdentityDbContextFactory / IdentityModuleExtensions
        // / IHostPro.MigrationRunner/Program.cs (Architecture Principles,
        // Section 10) — this is also what fixes "permission denied for
        // schema public" when MigrateAsync() runs below via this helper.
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
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
