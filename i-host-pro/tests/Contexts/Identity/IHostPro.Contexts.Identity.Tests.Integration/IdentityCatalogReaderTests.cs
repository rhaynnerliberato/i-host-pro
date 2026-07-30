using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Infrastructure.Catalog;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// <see cref="IdentityCatalogReader"/> against a real PostgreSQL instance
/// (Incremento 3, Checkpoint 3) — proves the reader genuinely reads the
/// persisted <c>roles</c>/<c>role_permissions</c>/<c>permissions</c> tables
/// (via the real migration, exactly as production applies it), never
/// <c>IdentityCatalogSeed</c>'s in-memory list, and that — unlike
/// <c>PermissionReader</c> (Checkpoint 2) — it is never cached: a direct
/// change in PostgreSQL is visible on the very next call.
/// </summary>
public class IdentityCatalogReaderTests : IClassFixture<IdentityCatalogReaderTests.Fixture>
{
    private readonly string _appConnectionString;

    public IdentityCatalogReaderTests(Fixture fixture) => _appConnectionString = fixture.AppConnectionString;

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

    private IdentityCatalogReader CreateReader() => new(CreateDbContext(_appConnectionString));

    // ---- Roles ------------------------------------------------------------

    [Fact]
    public async Task All_persisted_roles_are_returned()
    {
        var reader = CreateReader();

        var roles = await reader.ListRolesAsync(CancellationToken.None);

        roles.Select(r => r.Code).Should().BeEquivalentTo(IdentityCatalogSeed.Roles.Select(r => r.Id));
    }

    [Fact]
    public async Task Each_role_has_exactly_its_persisted_permissions()
    {
        var reader = CreateReader();
        var expectedAdminCodes = IdentityCatalogSeed.RolePermissions
            .Where(rp => rp.RoleCode == "ADMIN")
            .Select(rp => rp.PermissionCode)
            .Distinct(StringComparer.Ordinal);

        var roles = await reader.ListRolesAsync(CancellationToken.None);

        var admin = roles.Should().ContainSingle(r => r.Code == "ADMIN").Subject;
        admin.Name.Should().Be("Administrador");
        admin.PermissionCodes.Should().BeEquivalentTo(expectedAdminCodes);
    }

    [Fact]
    public async Task A_role_with_no_permission_returns_an_empty_collection()
    {
        // SYSTEM and INTEGRATION are seeded with zero permissions
        // (IdentityCatalogSeed's own documented, deliberate design).
        var reader = CreateReader();

        var roles = await reader.ListRolesAsync(CancellationToken.None);

        var system = roles.Should().ContainSingle(r => r.Code == "SYSTEM").Subject;
        system.PermissionCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task Roles_are_ordered_deterministically_by_code()
    {
        var reader = CreateReader();

        var first = await reader.ListRolesAsync(CancellationToken.None);
        var second = await reader.ListRolesAsync(CancellationToken.None);

        var expectedOrder = IdentityCatalogSeed.Roles.Select(r => r.Id).OrderBy(c => c, StringComparer.Ordinal);
        first.Select(r => r.Code).Should().Equal(expectedOrder);
        second.Select(r => r.Code).Should().Equal(first.Select(r => r.Code));
    }

    [Fact]
    public async Task No_role_or_permission_code_is_duplicated()
    {
        var reader = CreateReader();

        var roles = await reader.ListRolesAsync(CancellationToken.None);

        roles.Select(r => r.Code).Should().OnlyHaveUniqueItems();
        foreach (var role in roles)
            role.PermissionCodes.Should().OnlyHaveUniqueItems();
    }

    // ---- Permissions --------------------------------------------------------

    [Fact]
    public async Task All_persisted_permissions_are_returned()
    {
        var reader = CreateReader();

        var permissions = await reader.ListPermissionsAsync(CancellationToken.None);

        permissions.Select(p => p.Code).Should().BeEquivalentTo(IdentityCatalogSeed.Permissions.Select(p => p.Id));
    }

    [Fact]
    public async Task Permission_fields_match_the_persisted_row()
    {
        var reader = CreateReader();

        var permissions = await reader.ListPermissionsAsync(CancellationToken.None);

        var usersManage = permissions.Should().ContainSingle(p => p.Code == "USERS:MANAGE").Subject;
        usersManage.Resource.Should().Be("USERS");
        usersManage.Action.Should().Be("MANAGE");
        usersManage.Scope.Should().BeNull();

        var propertiesReadOwnOwner = permissions.Should().ContainSingle(p => p.Code == "PROPERTIES:READ:OWN_OWNER").Subject;
        propertiesReadOwnOwner.Scope.Should().Be("OWN_OWNER");
    }

    [Fact]
    public async Task Permissions_are_ordered_deterministically_by_code()
    {
        var reader = CreateReader();

        var first = await reader.ListPermissionsAsync(CancellationToken.None);
        var second = await reader.ListPermissionsAsync(CancellationToken.None);

        var expectedOrder = IdentityCatalogSeed.Permissions.Select(p => p.Id).OrderBy(c => c, StringComparer.Ordinal);
        first.Select(p => p.Code).Should().Equal(expectedOrder);
        second.Select(p => p.Code).Should().Equal(first.Select(p => p.Code));
    }

    [Fact]
    public async Task No_permission_code_is_duplicated()
    {
        var reader = CreateReader();

        var permissions = await reader.ListPermissionsAsync(CancellationToken.None);

        permissions.Select(p => p.Code).Should().OnlyHaveUniqueItems();
    }

    // ---- No caching: reflects the database immediately -----------------------

    [Fact]
    public async Task A_direct_database_change_is_reflected_on_the_very_next_call()
    {
        var roleCode = $"TESTROLE{Guid.NewGuid():N}"[..20];
        var reader = CreateReader();

        var before = await reader.ListRolesAsync(CancellationToken.None);
        before.Should().NotContain(r => r.Code == roleCode);

        await InsertRoleAsync(roleCode, "Temporary Test Role");

        var after = await reader.ListRolesAsync(CancellationToken.None);
        after.Should().ContainSingle(r => r.Code == roleCode);
    }

    // ---- No tenant-owned table is touched -------------------------------------

    [Fact]
    public async Task Both_listings_succeed_with_no_tenant_ever_resolved()
    {
        // TenantContext here is deliberately never SetTenant(...)'d — proves
        // neither query needs a resolved tenant, confirming roles/permissions/
        // role_permissions are genuinely not tenant-owned (unlike users/
        // sessions/user_roles/refresh_tokens, which would fail-closed to zero
        // rows or throw without one).
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        await using var dbContext = new IdentityDbContext(options, new TenantContext()); // unresolved tenant
        var reader = new IdentityCatalogReader(dbContext);

        var roles = await reader.ListRolesAsync(CancellationToken.None);
        var permissions = await reader.ListPermissionsAsync(CancellationToken.None);

        roles.Should().NotBeEmpty();
        permissions.Should().NotBeEmpty();
    }

    // ---- Read-only ------------------------------------------------------------

    [Fact]
    public async Task Neither_query_alters_any_row()
    {
        var reader = CreateReader();
        var before = await CountCatalogRowsAsync();

        await reader.ListRolesAsync(CancellationToken.None);
        await reader.ListPermissionsAsync(CancellationToken.None);

        var after = await CountCatalogRowsAsync();
        after.Should().Be(before);
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task InsertRoleAsync(string code, string displayName)
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO identity.roles (code, display_name) VALUES (@code, @displayName);";
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("displayName", displayName);
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
