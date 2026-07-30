using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.Identity.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// <see cref="UserAdministrationReader"/> against a real PostgreSQL instance
/// (Incremento 3, Checkpoint 5) — proves pagination/total, case-insensitive
/// search by name and e-mail (including the specific regression covered by
/// <see cref="Search_by_email_is_case_insensitive_and_matches_a_partial_fragment"/>:
/// <c>NormalizedEmail</c> is stored lowercase by <c>EmailNormalizer</c>, so the
/// reader must lower — not upper — the search term before comparing),
/// deterministic ordering, ordinally-sorted per-user roles and tenant
/// isolation all hold against genuinely persisted rows. Mirrors
/// <see cref="IdentityCatalogReaderTests"/>'s structure for the analogous
/// catalog reader (Checkpoint 3), but — unlike roles/permissions, which are
/// platform catalog tables — <c>Users</c>/<c>UserRoles</c> are tenant-owned,
/// so every call through the reader must run inside a transaction with
/// <c>app.tenant_id</c> set via <c>SET LOCAL</c> (exactly like every other
/// tenant-owned-table read in this project, e.g.
/// <see cref="CreateUserCommandHandlerTests"/>'s verification blocks) —
/// otherwise Row-Level Security fails closed to zero rows regardless of what
/// <see cref="ITenantContext"/> says, independently of the reader's own
/// correctness. No Wolverine outbox is provisioned: this reader never touches
/// it.
/// </summary>
public class UserAdministrationReaderTests : IClassFixture<UserAdministrationReaderTests.Fixture>
{
    private readonly string _appConnectionString;

    public UserAdministrationReaderTests(Fixture fixture) => _appConnectionString = fixture.AppConnectionString;

    /// <summary>
    /// Started once per test class, not once per test method — same rationale
    /// as <see cref="IdentityCatalogReaderTests.Fixture"/>.
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

            await using var migratorDbContext = CreateDbContext(migratorConnectionString, tenantId: null);
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();
    }

    private static IdentityDbContext CreateDbContext(string connectionString, Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        var tenantContext = new TenantContext();
        if (tenantId.HasValue)
            tenantContext.SetTenant(tenantId.Value);

        return new IdentityDbContext(options, tenantContext);
    }

    private static UserAdministrationReader CreateReader(IdentityDbContext dbContext, int defaultPageSize = 20, int maxPageSize = 100) =>
        new(dbContext, new UserListingSettingsProvider(Options.Create(new UserListingOptions
        {
            DefaultPageSize = defaultPageSize,
            MaxPageSize = maxPageSize,
        })));

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Seeding --------------------------------------------------------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenantId;
    }

    private async Task<Guid> SeedUserAsync(
        Guid tenantId, string fullName, string email, UserStatus status = UserStatus.Active, string[]? roleCodes = null)
    {
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, "Correct-Horse-Battery-Staple-42!"));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), fullName, hash, now);
        if (status == UserStatus.Blocked)
            user.Block(now);
        dbContext.Users.Add(user);

        // AssignedByUserId is deliberately null here — this Fixture only
        // exercises the reader, not the FK_user_roles_users_tenant_id_assigned_by_user_id
        // constraint CreateUserCommandHandlerTests already covers; a null actor
        // is a valid, unconstrained value for that nullable column.
        foreach (var roleCode in roleCodes ?? [])
            dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, roleCode, now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    // ---- Pagination -----------------------------------------------------------

    [Fact]
    public async Task Pagination_returns_the_correct_page_and_total_count()
    {
        var tenantId = await SeedTenantAsync();
        for (var i = 0; i < 5; i++)
            await SeedUserAsync(tenantId, $"User {i}", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var page1 = await reader.ListAsync(page: 1, pageSize: 2, search: null, status: null, CancellationToken.None);
        var page2 = await reader.ListAsync(page: 2, pageSize: 2, search: null, status: null, CancellationToken.None);
        var page3 = await reader.ListAsync(page: 3, pageSize: 2, search: null, status: null, CancellationToken.None);

        page1.TotalCount.Should().Be(5);
        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page3.Items.Should().HaveCount(1);
        var allIds = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(u => u.Id).ToArray();
        allIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Page_and_pageSize_are_clamped_when_out_of_range()
    {
        var tenantId = await SeedTenantAsync();
        await SeedUserAsync(tenantId, "Solo User", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext, maxPageSize: 100);

        var result = await reader.ListAsync(page: 0, pageSize: 99999, search: null, status: null, CancellationToken.None);

        result.Page.Should().Be(1);
        result.PageSize.Should().Be(100);
    }

    // ---- Search -----------------------------------------------------------------

    [Fact]
    public async Task Search_by_full_name_is_case_insensitive_and_matches_a_partial_fragment()
    {
        var tenantId = await SeedTenantAsync();
        await SeedUserAsync(tenantId, "Alice Wonderland", $"{Guid.NewGuid():N}@ihostpro.com");
        await SeedUserAsync(tenantId, "Bob Builder", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(page: 1, pageSize: 20, search: "WONDER", status: null, CancellationToken.None);

        result.Items.Should().ContainSingle(u => u.FullName == "Alice Wonderland");
    }

    [Fact]
    public async Task Search_by_email_is_case_insensitive_and_matches_a_partial_fragment()
    {
        // Regression test: NormalizedEmail is persisted lowercase
        // (EmailNormalizer.Normalize), so the reader must lower the search
        // term the same way before comparing — an upper-cased search term
        // would otherwise silently never match.
        var tenantId = await SeedTenantAsync();
        var uniqueLocalPart = Guid.NewGuid().ToString("N");
        await SeedUserAsync(tenantId, "Someone", $"{uniqueLocalPart}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(
            page: 1, pageSize: 20, search: uniqueLocalPart.ToUpperInvariant(), status: null, CancellationToken.None);

        result.Items.Should().ContainSingle(u => u.Email == $"{uniqueLocalPart}@ihostpro.com");
    }

    // ---- Status filter ------------------------------------------------------

    [Fact]
    public async Task Status_filter_returns_only_users_with_that_exact_status()
    {
        var tenantId = await SeedTenantAsync();
        var activeId = await SeedUserAsync(tenantId, "Active User", $"{Guid.NewGuid():N}@ihostpro.com");
        var blockedId = await SeedUserAsync(tenantId, "Blocked User", $"{Guid.NewGuid():N}@ihostpro.com", UserStatus.Blocked);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(page: 1, pageSize: 20, search: null, status: UserStatus.Blocked, CancellationToken.None);

        result.Items.Select(u => u.Id).Should().Equal(blockedId);
        result.Items.Single().Status.Should().Be("Blocked");
        result.Items.Should().NotContain(u => u.Id == activeId);
    }

    // ---- Ordering -----------------------------------------------------------------

    [Fact]
    public async Task Results_are_ordered_deterministically_by_normalized_email()
    {
        var tenantId = await SeedTenantAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedUserAsync(tenantId, "Charlie", $"charlie-{suffix}@ihostpro.com");
        await SeedUserAsync(tenantId, "Alpha", $"alpha-{suffix}@ihostpro.com");
        await SeedUserAsync(tenantId, "Bravo", $"bravo-{suffix}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var first = await reader.ListAsync(page: 1, pageSize: 20, search: suffix, status: null, CancellationToken.None);
        var second = await reader.ListAsync(page: 1, pageSize: 20, search: suffix, status: null, CancellationToken.None);

        first.Items.Select(u => u.Email).Should().Equal(
            $"alpha-{suffix}@ihostpro.com", $"bravo-{suffix}@ihostpro.com", $"charlie-{suffix}@ihostpro.com");
        second.Items.Select(u => u.Id).Should().Equal(first.Items.Select(u => u.Id));
    }

    // ---- Roles --------------------------------------------------------------------

    [Fact]
    public async Task Roles_are_ordinally_sorted_per_user()
    {
        // A genuinely duplicate (UserId, RoleCode) pair cannot be persisted at
        // all — EF's own change tracker rejects a second UserRole instance
        // with the same key before it ever reaches PostgreSQL — so this only
        // exercises ordering, not the reader's Distinct() call (which exists
        // purely as defense in depth against a state the schema already
        // makes impossible).
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(
            tenantId, "Multi Role", $"{Guid.NewGuid():N}@ihostpro.com", roleCodes: ["OPERATOR", "ADMIN"]);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(page: 1, pageSize: 20, search: null, status: null, CancellationToken.None);

        result.Items.Single(u => u.Id == userId).Roles.Should().Equal("ADMIN", "OPERATOR");
    }

    [Fact]
    public async Task A_user_with_no_role_returns_an_empty_roles_collection()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, "No Role", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(page: 1, pageSize: 20, search: null, status: null, CancellationToken.None);

        result.Items.Single(u => u.Id == userId).Roles.Should().BeEmpty();
    }

    // ---- Tenant isolation (RLS) -------------------------------------------------

    [Fact]
    public async Task ListAsync_never_returns_a_user_belonging_to_a_different_tenant()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userInTenantA = await SeedUserAsync(tenantA, "Tenant A User", $"{Guid.NewGuid():N}@ihostpro.com");
        var userInTenantB = await SeedUserAsync(tenantB, "Tenant B User", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantA);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantA);
        var reader = CreateReader(dbContext);

        var result = await reader.ListAsync(page: 1, pageSize: 20, search: null, status: null, CancellationToken.None);

        // Proves real discrimination, not RLS merely blocking everything:
        // tenant A's own user IS visible, tenant B's is not.
        result.Items.Should().Contain(u => u.Id == userInTenantA);
        result.Items.Should().NotContain(u => u.Id == userInTenantB);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_a_user_belonging_to_a_different_tenant()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userInTenantB = await SeedUserAsync(tenantB, "Tenant B User", $"{Guid.NewGuid():N}@ihostpro.com");
        await using var dbContext = CreateDbContext(_appConnectionString, tenantA);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantA);
        var reader = CreateReader(dbContext);

        var result = await reader.GetByIdAsync(userInTenantB, CancellationToken.None);

        result.Should().BeNull();
    }

    // ---- GetById --------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_returns_the_user_with_its_roles()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(
            tenantId, "Detail User", $"{Guid.NewGuid():N}@ihostpro.com", roleCodes: ["ADMIN"]);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.GetByIdAsync(userId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
        result.Roles.Should().Equal("ADMIN");
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_a_nonexistent_user()
    {
        var tenantId = await SeedTenantAsync();
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = CreateReader(dbContext);

        var result = await reader.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    // ---- Read-only ------------------------------------------------------------

    [Fact]
    public async Task Neither_query_mutates_any_row()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, "Untouched", $"{Guid.NewGuid():N}@ihostpro.com");

        await using (var dbContext = CreateDbContext(_appConnectionString, tenantId))
        await using (var transaction = await dbContext.Database.BeginTransactionAsync())
        {
            await SetPostgresTenantAsync(dbContext, tenantId);
            var reader = CreateReader(dbContext);

            await reader.ListAsync(page: 1, pageSize: 20, search: null, status: null, CancellationToken.None);
            await reader.GetByIdAsync(userId, CancellationToken.None);
        }

        await using var verifyContext = CreateDbContext(_appConnectionString, tenantId);
        await using var verifyTransaction = await verifyContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(verifyContext, tenantId);
        var user = await verifyContext.Users.SingleAsync(u => u.Id == userId);
        user.UpdatedAt.Should().Be(user.CreatedAt);
    }
}
