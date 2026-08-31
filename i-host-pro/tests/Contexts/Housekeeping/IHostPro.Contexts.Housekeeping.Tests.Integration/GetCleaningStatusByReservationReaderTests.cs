using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Real-Postgres proof for <see cref="CleaningReader.GetStatusByReservationIdAsync"/>
/// (Fase 11, Checkpoint 3 — AI Agent's own <c>GetCleaningStatus</c> Read
/// Tool). Mirrors <c>CleaningReadinessReaderTests</c>' own reader-test
/// structure exactly.
/// </summary>
public class GetCleaningStatusByReservationReaderTests : IClassFixture<HousekeepingFoundationTests.Fixture>
{
    private readonly string _appConnectionString;

    public GetCleaningStatusByReservationReaderTests(HousekeepingFoundationTests.Fixture fixture) =>
        _appConnectionString = fixture.AppConnectionString;

    [Fact]
    public async Task Returns_null_when_no_cleaning_exists_for_the_reservation()
    {
        var result = await ResolveAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_the_single_cleanings_status_and_available_timestamps()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await SeedCleaningAsync(tenantId, reservationId, now, cleaning => cleaning.Assign(Guid.NewGuid(), now));

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Assigned");
        result.CompletedAtUtc.Should().BeNull("no invented completion fact for a cleaning that has not completed");
    }

    [Fact]
    public async Task Multiple_cleanings_resolve_to_the_most_recent_by_CreatedAtUtc_never_by_status()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var earlier = DateTimeOffset.UtcNow.AddDays(-2);
        var later = DateTimeOffset.UtcNow.AddDays(-1);

        // The EARLIER (automated) cleaning ends up Completed — the tie-break
        // must still pick the LATER cleaning, proving status is never used
        // as a priority signal. The second cleaning must be manually
        // created (non-null CreatedByUserId) — only one AUTOMATED
        // (CreatedByUserId == null) Cleaning may exist per Reservation (the
        // pre-existing partial unique index this table already enforces).
        await SeedCleaningAsync(tenantId, reservationId, earlier, mutate: cleaning =>
        {
            cleaning.Assign(Guid.NewGuid(), earlier);
            cleaning.Start(earlier);
            cleaning.StartInspection(earlier);
            cleaning.Complete(earlier);
        }, createdByUserId: null);
        await SeedCleaningAsync(tenantId, reservationId, later, mutate: cleaning => { }, createdByUserId: Guid.NewGuid());

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Pending", "the most recent cleaning by CreatedAtUtc wins, regardless of the older cleaning's Completed status");
    }

    [Fact]
    public async Task Wrong_tenant_never_resolves_another_tenants_cleaning()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, reservationId, DateTimeOffset.UtcNow, cleaning => { });

        var result = await ResolveAsync(Guid.NewGuid(), reservationId);

        result.Should().BeNull();
    }

    // ---- Helpers ----

    private async Task SeedCleaningAsync(
        Guid tenantId, Guid reservationId, DateTimeOffset createdAtUtc, Action<Cleaning> mutate, Guid? createdByUserId = null)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateAppDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), reservationId, createdByUserId, createdAtUtc);
        mutate(cleaning);
        dbContext.Cleanings.Add(cleaning);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<CleaningStatusResult?> ResolveAsync(Guid tenantId, Guid reservationId)
    {
        await using var dbContext = CreateAppDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);
        var reader = new CleaningReader(dbContext);

        return await reader.GetStatusByReservationIdAsync(reservationId, CancellationToken.None);
    }

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private HousekeepingDbContext CreateAppDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
    }
}
