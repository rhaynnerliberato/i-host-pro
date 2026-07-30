using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Infrastructure.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// <see cref="PermissionReader"/> against a real PostgreSQL instance
/// (Incremento 3, Checkpoint 2) — proves the reader genuinely reads the
/// persisted <c>roles</c>/<c>role_permissions</c>/<c>permissions</c> tables
/// (via the real migration, exactly as production applies it), never a
/// parallel in-memory list, and that <see cref="RolePermissionCache"/>'s TTL
/// is real and controllable. No outbox/Wolverine provisioning — unlike
/// <c>AuthEndpointsTests.Fixture</c>, this reader never publishes an
/// Integration Event, so none of that machinery is needed here.
/// </summary>
public class PermissionReaderTests : IClassFixture<PermissionReaderTests.Fixture>
{
    private readonly string _appConnectionString;

    public PermissionReaderTests(Fixture fixture) => _appConnectionString = fixture.AppConnectionString;

    /// <summary>
    /// Started once per test class, not once per test method — same rationale
    /// as <c>IdentityRowLevelSecurityTests.Fixture</c> (Docker daemon load).
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _postgresContainer.StartAsync();

            var adminConnectionString = _postgresContainer.GetConnectionString();

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

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(migratorConnectionString);
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();
    }

    private static IdentityDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        // No real tenant is ever resolved/used — roles/permissions/role_permissions
        // are platform catalog tables, never tenant-owned (Architecture
        // Principles §7), so the Global Query Filter never applies to them.
        return new IdentityDbContext(options, new TenantContext());
    }

    private PermissionReader CreateReader(RolePermissionCache? cache = null) =>
        new(CreateDbContext(_appConnectionString), cache ?? NewCache());

    private static RolePermissionCache NewCache(TimeProvider? timeProvider = null) =>
        new(timeProvider ?? TimeProvider.System, Options.Create(new PermissionCacheOptions { Lifetime = TimeSpan.FromMinutes(5) }));

    // ---- Reads the real seeded catalog ---------------------------------

    [Fact]
    public async Task ADMIN_receives_exactly_the_persisted_codes()
    {
        var reader = CreateReader();
        var expected = IdentityCatalogSeed.RolePermissions
            .Where(rp => rp.RoleCode == "ADMIN")
            .Select(rp => rp.PermissionCode)
            .Distinct(StringComparer.Ordinal);

        var result = await reader.GetPermissionCodesAsync(["ADMIN"], CancellationToken.None);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task A_role_without_USERS_MANAGE_does_not_receive_it()
    {
        var reader = CreateReader();

        var result = await reader.GetPermissionCodesAsync(["HOUSEKEEPER"], CancellationToken.None);

        result.Should().NotContain("USERS:MANAGE");
    }

    [Fact]
    public async Task An_unknown_role_returns_an_empty_collection()
    {
        var reader = CreateReader();

        var result = await reader.GetPermissionCodesAsync(["NOT_A_REAL_ROLE"], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_roles_return_the_union_without_duplicates()
    {
        var reader = CreateReader();
        var expectedUnion = IdentityCatalogSeed.RolePermissions
            .Where(rp => rp.RoleCode is "ADMIN" or "OPERATOR")
            .Select(rp => rp.PermissionCode)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // ADMIN and OPERATOR are both granted AUDIT:READ in the seeded catalog
        // — a real overlap, not a coincidence of this test, so a naive
        // concatenation (rather than a true union) would be caught here.
        expectedUnion.Should().Contain("AUDIT:READ");

        var result = await reader.GetPermissionCodesAsync(["ADMIN", "OPERATOR"], CancellationToken.None);

        result.Should().OnlyHaveUniqueItems();
        result.Should().BeEquivalentTo(expectedUnion);
    }

    [Fact]
    public async Task Values_come_from_the_persisted_tables_never_from_a_parallel_in_memory_list()
    {
        // roleCode/permissionCode below exist ONLY as rows inserted directly
        // into PostgreSQL by this test — neither appears anywhere in
        // IdentityCatalogSeed's C# source. PermissionReader returning it
        // proves the read is genuinely against the database, not against any
        // in-memory catalog a future refactor might introduce.
        var roleCode = $"TESTROLE{Guid.NewGuid():N}"[..20];
        var permissionCode = $"TEST:{Guid.NewGuid():N}"[..20];
        await SeedRolePermissionAsync(roleCode, permissionCode);
        var reader = CreateReader();

        var result = await reader.GetPermissionCodesAsync([roleCode], CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(permissionCode);
    }

    [Fact]
    public async Task The_query_never_alters_data()
    {
        var reader = CreateReader();
        var before = await CountCatalogRowsAsync();

        await reader.GetPermissionCodesAsync(["ADMIN", "OPERATOR", "HOUSEKEEPER", "NOT_A_REAL_ROLE"], CancellationToken.None);

        var after = await CountCatalogRowsAsync();
        after.Should().Be(before);
    }

    // ---- Cache TTL is real and testable --------------------------------

    [Fact]
    public async Task Cached_result_is_reused_within_the_TTL_then_reflects_the_database_after_it_expires()
    {
        var roleCode = $"TESTROLE{Guid.NewGuid():N}"[..20];
        var permissionCode = $"TEST:{Guid.NewGuid():N}"[..20];
        await SeedRolePermissionAsync(roleCode, permissionCode);
        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var cache = new RolePermissionCache(timeProvider, Options.Create(new PermissionCacheOptions { Lifetime = TimeSpan.FromMinutes(1) }));
        var reader = CreateReader(cache);

        var firstRead = await reader.GetPermissionCodesAsync([roleCode], CancellationToken.None);
        firstRead.Should().Contain(permissionCode);

        // Bypass the reader entirely: remove the grant directly in PostgreSQL.
        await RemoveRolePermissionAsync(roleCode, permissionCode);

        var stillWithinTtl = await reader.GetPermissionCodesAsync([roleCode], CancellationToken.None);
        stillWithinTtl.Should().Contain(permissionCode, "the cache entry has not expired yet, so the stale value is served");

        timeProvider.Advance(TimeSpan.FromMinutes(2));

        var afterTtlExpired = await reader.GetPermissionCodesAsync([roleCode], CancellationToken.None);
        afterTtlExpired.Should().NotContain(permissionCode, "the cache expired, forcing a fresh read that reflects the current database state");
    }

    // ---- Seeding / inspection helpers -----------------------------------

    private async Task SeedRolePermissionAsync(string roleCode, string permissionCode)
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identity.roles (code, display_name) VALUES (@roleCode, 'Test Role');
            INSERT INTO identity.permissions (code, resource, action, scope) VALUES (@permissionCode, 'TEST', 'TEST', NULL);
            INSERT INTO identity.role_permissions (role_code, permission_code) VALUES (@roleCode, @permissionCode);
            """;
        command.Parameters.AddWithValue("roleCode", roleCode);
        command.Parameters.AddWithValue("permissionCode", permissionCode);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RemoveRolePermissionAsync(string roleCode, string permissionCode)
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM identity.role_permissions WHERE role_code = @roleCode AND permission_code = @permissionCode;";
        command.Parameters.AddWithValue("roleCode", roleCode);
        command.Parameters.AddWithValue("permissionCode", permissionCode);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(long Roles, long Permissions, long RolePermissions)> CountCatalogRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        async Task<long> CountAsync(string table)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM identity.{table}";
            return (long)(await command.ExecuteScalarAsync())!;
        }

        return (await CountAsync("roles"), await CountAsync("permissions"), await CountAsync("role_permissions"));
    }
}

/// <summary>Mutable <see cref="TimeProvider"/> test double — unlike a fixed one, its clock can be advanced mid-test to exercise TTL expiry deterministically.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}
