using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// <see cref="IdentityUserEligibilityReader"/> against a real PostgreSQL
/// instance (Checkpoint 5 plan, item 18) — proves the public contract Property
/// Management's Ownership feature relies on: same-tenant active user holding
/// the required role is eligible; an inactive (blocked) user or one missing
/// the role is reported as such (never thrown); a nonexistent user, or one
/// belonging to a different tenant, is indistinguishable and returns
/// <c>null</c> — this is also the RLS fail-closed proof, since
/// <see cref="TenantB_users_are_invisible_when_queried_with_TenantA_context"/>
/// proves a genuinely-existing row is hidden rather than throwing or leaking.
/// The reader opens its OWN short-lived read-only transaction internally
/// (<see cref="IdentityUserEligibilityReader.GetAsync"/>), so — unlike
/// <see cref="UserAdministrationReaderTests"/>, whose reader expects the
/// caller to have already opened the tenant-scoped transaction — every test
/// here passes a freshly-constructed <see cref="IdentityDbContext"/> with no
/// transaction open, exactly as the real caller (<c>LinkPropertyOwnerCommandHandler</c>,
/// a different Bounded Context) would.
/// </summary>
public class IdentityUserEligibilityReaderTests : IClassFixture<IdentityUserEligibilityReaderTests.Fixture>
{
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _appConnectionString;

    public IdentityUserEligibilityReaderTests(Fixture fixture) => _appConnectionString = fixture.AppConnectionString;

    /// <summary>Started once per test class — see <see cref="UserAdministrationReaderTests.Fixture"/>'s doc comment for the full rationale.</summary>
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

    private async Task<Guid> SeedUserAsync(Guid tenantId, bool blocked = false, string[]? roleCodes = null)
    {
        await using var dbContext = CreateDbContext(_appConnectionString, tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, now);
        if (blocked)
            user.Block(now);
        dbContext.Users.Add(user);

        foreach (var roleCode in roleCodes ?? [])
            dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, roleCode, now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    /// <summary>
    /// The ambient <see cref="IdentityDbContext"/> must be constructed with the
    /// SAME tenant as the one the caller will pass to <c>GetAsync</c> — in
    /// production both are resolved from the same JWT claim
    /// (<c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c>), and
    /// <see cref="IdentityDbContext"/> inherits a mandatory tenant Global Query
    /// Filter from <c>BaseDbContext</c> that independently enforces this,
    /// on top of (not instead of) PostgreSQL's own Row-Level Security.
    /// </summary>
    private static IdentityUserEligibilityReader CreateReader(string connectionString, Guid ambientTenantId) =>
        new(CreateDbContext(connectionString, ambientTenantId));

    // ---- Eligible ---------------------------------------------------------

    [Fact]
    public async Task An_active_user_holding_the_required_role_is_reported_eligible()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        var reader = CreateReader(_appConnectionString, tenantId);

        var result = await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(userId);
        result.IsActive.Should().BeTrue();
        result.HasRequiredRole.Should().BeTrue();
    }

    // ---- Not eligible, but not null (the user exists) ---------------------

    [Fact]
    public async Task An_active_user_without_the_required_role_is_reported_active_but_not_eligible()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, roleCodes: ["OPERATOR"]);
        var reader = CreateReader(_appConnectionString, tenantId);

        var result = await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsActive.Should().BeTrue();
        result.HasRequiredRole.Should().BeFalse();
    }

    [Fact]
    public async Task A_blocked_user_holding_the_required_role_is_reported_inactive()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, blocked: true, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        var reader = CreateReader(_appConnectionString, tenantId);

        var result = await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsActive.Should().BeFalse();
        result.HasRequiredRole.Should().BeTrue();
    }

    [Fact]
    public async Task A_blocked_user_without_the_required_role_is_reported_inactive_and_not_eligible()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, blocked: true);
        var reader = CreateReader(_appConnectionString, tenantId);

        var result = await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsActive.Should().BeFalse();
        result.HasRequiredRole.Should().BeFalse();
    }

    // ---- Null (nonexistent / cross-tenant) ---------------------------------

    [Fact]
    public async Task A_nonexistent_user_returns_null()
    {
        var tenantId = await SeedTenantAsync();
        var reader = CreateReader(_appConnectionString, tenantId);

        var result = await reader.GetAsync(tenantId, Guid.NewGuid(), IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().BeNull();
    }

    /// <summary>
    /// The RLS fail-closed proof: <paramref name="userInTenantB"/> genuinely
    /// exists (with the role and active status that WOULD make it eligible
    /// under its own tenant) but is queried under Tenant A's id — PostgreSQL's
    /// Row-Level Security hides the row entirely, and the reader reports this
    /// exactly like a nonexistent user, never throwing and never leaking a
    /// partial result.
    /// </summary>
    [Fact]
    public async Task TenantB_users_are_invisible_when_queried_with_TenantA_context()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var userInTenantB = await SeedUserAsync(tenantB, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        var reader = CreateReader(_appConnectionString, tenantA);

        var result = await reader.GetAsync(tenantA, userInTenantB, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        result.Should().BeNull();
    }

    // ---- Read-only ----------------------------------------------------------

    [Fact]
    public async Task The_eligibility_check_never_mutates_any_row()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        var reader = CreateReader(_appConnectionString, tenantId);

        await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, CancellationToken.None);

        await using var verifyContext = CreateDbContext(_appConnectionString, tenantId);
        await using var verifyTransaction = await verifyContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(verifyContext, tenantId);
        var user = await verifyContext.Users.SingleAsync(u => u.Id == userId);
        user.UpdatedAt.Should().Be(user.CreatedAt);
        (await verifyContext.UserRoles.CountAsync(ur => ur.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var tenantId = await SeedTenantAsync();
        var userId = await SeedUserAsync(tenantId, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        var reader = CreateReader(_appConnectionString, tenantId);
        using var cts = new CancellationTokenSource();

        var act = async () => await reader.GetAsync(tenantId, userId, IdentityRoleCodes.PropertyOwner, cts.Token);

        await act.Should().NotThrowAsync();
    }
}
