using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IHostPro.TenantProvisioning.Tests.Integration;

/// <summary>
/// Real-PostgreSQL coverage of <see cref="TenantProvisioner"/> (CP5.3D-C
/// corrective Decision Gate) — mirrors
/// IHostPro.Contexts.Identity.Tests.Integration.DevelopmentIdentitySeederTests'
/// fixture pattern (real roles, real migrated schema, real RLS) since this
/// tool exercises the exact same domain/persistence surface, just outside
/// Development.
/// </summary>
public class TenantProvisionerTests : IClassFixture<TenantProvisionerTests.Fixture>
{
    private readonly Fixture _fixture;

    public TenantProvisionerTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer Container { get; private set; } = null!;
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
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(migratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await Container.DisposeAsync();
    }

    private const string ValidPassword = "Tenant-Provision-Passw0rd!";

    private static TenantSlug NewSlug() => TenantSlug.Create($"prov-{Guid.NewGuid():N}"[..20]);
    private static string NewEmail() => $"admin-{Guid.NewGuid():N}@prov.local";

    private TenantProvisioner CreateProvisioner(out IdentityDbContext dbContext)
    {
        var tenantContext = new TenantContext();
        dbContext = CreateDbContext(_fixture.AppConnectionString, tenantContext);
        return new TenantProvisioner(dbContext, tenantContext, TimeProvider.System);
    }

    private static ProvisioningRequest NewRequest(TenantSlug slug, string email) =>
        new(slug, "Provisioning Test Tenant", email, "Provisioning Test Admin", ValidPassword);

    // ---- 1. New tenant + 2. New admin -------------------------------------

    [Fact]
    public async Task First_run_creates_a_new_tenant_and_a_new_admin_user()
    {
        var slug = NewSlug();
        var email = NewEmail();
        var provisioner = CreateProvisioner(out var dbContext);
        await using var _ = dbContext;

        var result = await provisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        result.TenantCreated.Should().BeTrue();
        result.UserCreated.Should().BeTrue();
        result.AdminRoleAssigned.Should().BeTrue();

        var tenant = await FindTenantAsync(slug);
        tenant.Should().NotBeNull();
        tenant!.Name.Should().Be("Provisioning Test Tenant");
    }

    // ---- 3. Idempotent second run + 4. Tenant not duplicated + 5. Admin not duplicated ----

    [Fact]
    public async Task Running_it_twice_sequentially_is_idempotent_and_creates_no_duplicates()
    {
        var slug = NewSlug();
        var email = NewEmail();

        var firstProvisioner = CreateProvisioner(out var firstDbContext);
        await using (firstDbContext)
            await firstProvisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        var secondProvisioner = CreateProvisioner(out var secondDbContext);
        ProvisioningResult secondResult;
        await using (secondDbContext)
            secondResult = await secondProvisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        secondResult.TenantCreated.Should().BeFalse("the tenant already existed from the first run");
        secondResult.UserCreated.Should().BeFalse("the admin already existed from the first run");
        secondResult.AdminRoleAssigned.Should().BeFalse("the ADMIN role was already assigned on the first run");

        (await CountTenantsAsync(slug)).Should().Be(1);
        var tenant = await FindTenantAsync(slug);
        (await CountUsersAsync(tenant!.Id, email)).Should().Be(1);
    }

    // ---- 6. Password/hash uses the real mechanism -------------------------

    [Fact]
    public async Task The_admin_password_is_hashed_with_the_real_Argon2_mechanism_and_verifies_successfully()
    {
        var slug = NewSlug();
        var email = NewEmail();
        var provisioner = CreateProvisioner(out var dbContext);
        await using var _ = dbContext;
        await provisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        var tenant = await FindTenantAsync(slug);
        var user = await FindUserAsync(tenant!.Id, email);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        hasher.VerifyHashedPassword(null!, user!.PasswordHash.Value, ValidPassword)
            .Should().Be(PasswordVerificationResult.Success);
    }

    // ---- 7. Admin receives the correct (ADMIN) role -----------------------

    [Fact]
    public async Task The_new_admin_is_assigned_exactly_the_ADMIN_role()
    {
        var slug = NewSlug();
        var email = NewEmail();
        var provisioner = CreateProvisioner(out var dbContext);
        await using var _ = dbContext;
        await provisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        var tenant = await FindTenantAsync(slug);
        var user = await FindUserAsync(tenant!.Id, email);
        var roles = await FindRoleCodesAsync(tenant.Id, user!.Id);

        roles.Should().BeEquivalentTo([AdminRole.Code]);
    }

    // ---- 5 (reconcile case). A pre-existing admin missing its role gets it back, nothing else touched ----

    [Fact]
    public async Task Reconciling_an_existing_admin_with_a_missing_role_adds_only_the_role_never_a_new_user_or_password()
    {
        var slug = NewSlug();
        var email = NewEmail();

        var firstProvisioner = CreateProvisioner(out var firstDbContext);
        Guid tenantId, userId;
        await using (firstDbContext)
        {
            var first = await firstProvisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);
            tenantId = first.TenantId;
            userId = first.UserId;
        }

        // Simulate role loss (e.g. accidental manual removal) without
        // touching the user/tenant themselves.
        await using (var dbContext = CreateDbContext(_fixture.AppConnectionString, TenantScoped(tenantId)))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
            var role = await dbContext.UserRoles.SingleAsync(r => r.TenantId == tenantId && r.UserId == userId);
            dbContext.UserRoles.Remove(role);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var reconcileProvisioner = CreateProvisioner(out var reconcileDbContext);
        ProvisioningResult reconcileResult;
        await using (reconcileDbContext)
            reconcileResult = await reconcileProvisioner.ProvisionAsync(NewRequest(slug, email), CancellationToken.None);

        reconcileResult.TenantCreated.Should().BeFalse();
        reconcileResult.UserCreated.Should().BeFalse();
        reconcileResult.AdminRoleAssigned.Should().BeTrue("the role was missing and should be reconciled");

        (await CountUsersAsync(tenantId, email)).Should().Be(1, "reconciliation must never create a second user");
        var roles = await FindRoleCodesAsync(tenantId, userId);
        roles.Should().BeEquivalentTo([AdminRole.Code]);
    }

    // ---- 8. Tenant isolation (RLS) preserved -------------------------------

    [Fact]
    public async Task Two_provisioned_tenants_cannot_see_each_others_users_through_RLS()
    {
        var slugA = NewSlug();
        var emailA = NewEmail();
        var slugB = NewSlug();
        var emailB = NewEmail();

        var provisionerA = CreateProvisioner(out var dbContextA);
        await using (dbContextA)
            await provisionerA.ProvisionAsync(NewRequest(slugA, emailA), CancellationToken.None);

        var provisionerB = CreateProvisioner(out var dbContextB);
        await using (dbContextB)
            await provisionerB.ProvisionAsync(NewRequest(slugB, emailB), CancellationToken.None);

        var tenantA = await FindTenantAsync(slugA);
        var tenantB = await FindTenantAsync(slugB);

        // Querying tenant A's users while scoped to tenant B's RLS context
        // must return zero rows, never tenant A's admin.
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, TenantScoped(tenantB!.Id));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantB.Id.ToString()}, true)");

        var crossTenantRead = await dbContext.Users.Where(u => u.TenantId == tenantA!.Id).ToListAsync();
        crossTenantRead.Should().BeEmpty();
    }

    // ---- 6/10. Invalid password is rejected by the real policy and never leaks ----

    [Fact]
    public async Task A_password_that_fails_the_real_policy_is_rejected_and_never_appears_in_the_exception()
    {
        var slug = NewSlug();
        const string distinctiveWeakPassword = "weak";
        var provisioner = CreateProvisioner(out var dbContext);
        await using var _ = dbContext;

        var act = async () => await provisioner.ProvisionAsync(
            NewRequest(slug, NewEmail()) with { AdminPassword = distinctiveWeakPassword }, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotContain(distinctiveWeakPassword);

        (await FindTenantAsync(slug)).Should().BeNull("validation must fail before any row is created, including the tenant");
    }

    // ---- DB helpers ---------------------------------------------------------

    private static ITenantContext TenantScoped(Guid tenantId)
    {
        var context = new TenantContext();
        context.SetTenant(tenantId);
        return context;
    }

    private async Task<Tenant?> FindTenantAsync(TenantSlug slug)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, new TenantContext());
        return await dbContext.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
    }

    private async Task<long> CountTenantsAsync(TenantSlug slug)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, new TenantContext());
        return await dbContext.Tenants.LongCountAsync(t => t.Slug == slug);
    }

    private async Task<User?> FindUserAsync(Guid tenantId, string email)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        var normalized = Email.Create(email).NormalizedValue;
        return await dbContext.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalized);
    }

    private async Task<long> CountUsersAsync(Guid tenantId, string email)
    {
        var user = await FindUserAsync(tenantId, email);
        return user is null ? 0 : 1;
    }

    private async Task<IReadOnlyList<string>> FindRoleCodesAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, TenantScoped(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
        return await dbContext.UserRoles
            .Where(r => r.TenantId == tenantId && r.UserId == userId)
            .Select(r => r.RoleCode)
            .ToListAsync();
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }
}
