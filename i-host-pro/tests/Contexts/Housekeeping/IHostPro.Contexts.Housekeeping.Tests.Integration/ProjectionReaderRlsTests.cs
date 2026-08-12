using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Focused Row-Level Security proof for
/// <see cref="PropertyReferenceProjectionReader"/>/<see cref="ReservationReferenceProjectionReader"/>
/// against real PostgreSQL (Fase 6, Checkpoint 6, gate §3) — the two readers
/// whose missing tenant-scoped transaction was Checkpoint 4's critical
/// defect (§8.1 of the homologation document). Reuses
/// <see cref="HousekeepingFoundationTests.Fixture"/> (already provisions a
/// real Postgres container, applies migrations as <c>ihostpro_migrator</c>,
/// and grants <c>ihostpro_app</c> exactly the runtime privileges production
/// uses) rather than standing up a second, duplicate fixture.
///
/// Every reader call below runs through <see cref="AppConnectionString"/> —
/// the <c>ihostpro_app</c> role, never <c>ihostpro_migrator</c> — proving
/// items 4/5 of the gate (the projection readers work, and never need to
/// bypass RLS/use an elevated role) at the reader level directly; the same
/// fact is also proven end-to-end at the HTTP level by
/// <c>HousekeepingEndpointsTests</c>'s own already-passing
/// Create/Assign/.../Complete lifecycle (Checkpoint 4, §8.3), which also
/// runs its entire host exclusively against <c>AppConnectionString</c>.
///
/// Every <see cref="HousekeepingDbContext"/> constructed below is given its
/// OWN correctly-resolved ambient <see cref="TenantContext"/> — mirroring a
/// real per-request DI scope exactly (<see cref="BaseDbContext"/>'s own
/// Global Query Filter, `entity.TenantId == _tenantContext.TenantId`, fails
/// closed to zero rows whenever that ambient tenant is unresolved, entirely
/// independent of RLS — see its own doc comment). The reader's own
/// short-lived throwaway <see cref="TenantContext"/> (used purely to satisfy
/// PostgreSQL's <c>SET LOCAL app.tenant_id</c>) is a second, independent
/// mechanism this file deliberately exercises in isolation for the
/// "tenant ausente" case, by giving the ambient DbContext a real, resolved
/// tenant that matches the seeded row (so the EF filter does not itself
/// mask the assertion) while calling the reader with an explicit
/// <see cref="Guid.Empty"/> tenantId.
/// </summary>
public class ProjectionReaderRlsTests : IClassFixture<HousekeepingFoundationTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ProjectionReaderRlsTests(HousekeepingFoundationTests.Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    // ---- PropertyReferenceProjectionReader -----------------------------------

    [Fact]
    public async Task IsKnownActivePropertyAsync_returns_true_for_the_tenant_that_owns_the_row()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyAsync(tenantId, propertyId);

        var reader = new PropertyReferenceProjectionReader(CreateAppDbContext(tenantId));

        var result = await reader.IsKnownActivePropertyAsync(tenantId, propertyId, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsKnownActivePropertyAsync_returns_false_for_a_different_tenant_even_though_the_row_exists()
    {
        var ownerTenantId = Guid.NewGuid();
        var callerTenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyAsync(ownerTenantId, propertyId);

        // The ambient DbContext tenant matches the CALLER (never the owner)
        // — exactly as a real request scope would be: an admin from
        // callerTenantId, whose own DbContext/JWT tenant is callerTenantId,
        // referencing a propertyId that only exists for a different tenant.
        var reader = new PropertyReferenceProjectionReader(CreateAppDbContext(callerTenantId));

        var result = await reader.IsKnownActivePropertyAsync(callerTenantId, propertyId, CancellationToken.None);

        result.Should().BeFalse("RLS must isolate the row to its own tenant, not merely EF Core's Global Query Filter");
    }

    [Fact]
    public async Task IsKnownActivePropertyAsync_fails_closed_when_the_explicit_tenantId_is_absent()
    {
        var ownerTenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyAsync(ownerTenantId, propertyId);

        // Ambient DbContext tenant is the REAL owner (so EF Core's own Global
        // Query Filter does not itself hide the row) — isolating this
        // assertion to RLS's own SET LOCAL app.tenant_id mechanism, which the
        // reader drives from its own throwaway TenantContext built from the
        // explicit tenantId parameter, never the ambient one.
        var reader = new PropertyReferenceProjectionReader(CreateAppDbContext(ownerTenantId));

        var result = await reader.IsKnownActivePropertyAsync(Guid.Empty, propertyId, CancellationToken.None);

        result.Should().BeFalse("Guid.Empty is never a real, provisioned tenant — RLS must fail closed, never accidentally match");
    }

    // ---- ReservationReferenceProjectionReader --------------------------------

    [Fact]
    public async Task ExistsAsync_returns_true_for_the_tenant_that_owns_the_row()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId);

        var reader = new ReservationReferenceProjectionReader(CreateAppDbContext(tenantId));

        var result = await reader.ExistsAsync(tenantId, reservationId, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_returns_false_for_a_different_tenant_even_though_the_row_exists()
    {
        var ownerTenantId = Guid.NewGuid();
        var callerTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(ownerTenantId, reservationId);

        var reader = new ReservationReferenceProjectionReader(CreateAppDbContext(callerTenantId));

        var result = await reader.ExistsAsync(callerTenantId, reservationId, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_fails_closed_when_the_explicit_tenantId_is_absent()
    {
        var ownerTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(ownerTenantId, reservationId);

        var reader = new ReservationReferenceProjectionReader(CreateAppDbContext(ownerTenantId));

        var result = await reader.ExistsAsync(Guid.Empty, reservationId, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ---- No residual tenant leaks across pooled connections ------------------

    [Fact]
    public async Task Consecutive_reads_for_different_tenants_on_separate_scopes_never_leak_a_residual_SET_LOCAL_value()
    {
        // Mirrors two consecutive, independent per-request DI scopes (never
        // the same DbContext instance — a real ITenantContext can only ever
        // resolve once per scope, exactly like production) that are very
        // likely to draw the SAME pooled physical Npgsql connection from the
        // ihostpro_app connection-string pool, back to back. SET LOCAL is
        // transaction-scoped and undone at COMMIT — if it ever leaked onto
        // the pooled physical connection itself, the second call below would
        // incorrectly see zero rows (its own row's tenant_id would no longer
        // match a stale session variable left over from the first scope).
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var propertyA = Guid.NewGuid();
        var propertyB = Guid.NewGuid();
        await SeedActivePropertyAsync(tenantA, propertyA);
        await SeedActivePropertyAsync(tenantB, propertyB);

        await using (var dbContextA1 = CreateAppDbContext(tenantA))
        {
            var readerA1 = new PropertyReferenceProjectionReader(dbContextA1);
            (await readerA1.IsKnownActivePropertyAsync(tenantA, propertyA, CancellationToken.None))
                .Should().BeTrue();
        }

        await using (var dbContextB = CreateAppDbContext(tenantB))
        {
            var readerB = new PropertyReferenceProjectionReader(dbContextB);
            (await readerB.IsKnownActivePropertyAsync(tenantB, propertyB, CancellationToken.None))
                .Should().BeTrue("a residual SET LOCAL from tenantA's scope would incorrectly hide tenantB's own row");
        }

        await using (var dbContextA2 = CreateAppDbContext(tenantA))
        {
            var readerA2 = new PropertyReferenceProjectionReader(dbContextA2);
            (await readerA2.IsKnownActivePropertyAsync(tenantA, propertyA, CancellationToken.None))
                .Should().BeTrue("a residual SET LOCAL from tenantB's scope would incorrectly hide tenantA's own row again");
        }
    }

    // ---- Seeding (ihostpro_migrator role — the test's own setup, not the ---
    // ---- behavior under test; mirrors HousekeepingEndpointsTests exactly) --

    private async Task SeedActivePropertyAsync(Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.PropertyProjection.Add(new PropertyProjectionEntry(tenantId, propertyId, isActive: true));
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedReservationAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.ReservationProjection.Add(new ReservationProjectionEntry(tenantId, reservationId));
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    /// <summary>Ambient DbContext tenant pre-resolved to <paramref name="tenantId"/> — mirrors a real per-request DI scope, never left unresolved.</summary>
    private HousekeepingDbContext CreateAppDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_appConnectionString, tenantContext);
    }

    private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
    }
}
