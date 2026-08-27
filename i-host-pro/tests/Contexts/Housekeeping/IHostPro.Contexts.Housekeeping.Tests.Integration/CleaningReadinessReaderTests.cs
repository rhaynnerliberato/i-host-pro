using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.GuestOperations;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Exercises <see cref="ICleaningReadinessReader"/> — ADR-024 amendment's
/// synchronous exception #8, Guest Operations → Housekeeping cleaning
/// readiness read (Fase 10, Checkpoint 3), Housekeeping's first-ever
/// synchronous exception — against a real PostgreSQL instance. Reuses
/// <see cref="HousekeepingFoundationTests.Fixture"/>, mirroring
/// <c>ProjectionReaderRlsTests</c>'s own reuse exactly.
/// </summary>
public class CleaningReadinessReaderTests : IClassFixture<HousekeepingFoundationTests.Fixture>
{
    private readonly string _appConnectionString;

    public CleaningReadinessReaderTests(HousekeepingFoundationTests.Fixture fixture) =>
        _appConnectionString = fixture.AppConnectionString;

    [Fact]
    public async Task IsCleaningCompletedAsync_returns_false_when_no_cleaning_exists_for_the_reservation()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        var result = await IsCompletedAsync(tenantId, reservationId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsCleaningCompletedAsync_returns_false_when_the_cleaning_exists_but_is_not_yet_completed()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, reservationId, CleaningState.Pending);

        var result = await IsCompletedAsync(tenantId, reservationId);

        result.Should().BeFalse("a Cleaning that exists but has not reached Completed must not be reported ready");
    }

    [Fact]
    public async Task IsCleaningCompletedAsync_returns_true_when_the_cleaning_is_completed()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, reservationId, CleaningState.Completed);

        var result = await IsCompletedAsync(tenantId, reservationId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCleaningCompletedAsync_returns_false_for_a_reservation_belonging_to_another_tenant()
    {
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedCleaningAsync(ownerTenantId, reservationId, CleaningState.Completed);

        var result = await IsCompletedAsync(otherTenantId, reservationId);

        result.Should().BeFalse("a cross-tenant reservationId must never report readiness (RLS)");
    }

    [Fact]
    public async Task IsCleaningCompletedAsync_returns_false_when_the_cleaning_is_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, reservationId, CleaningState.Cancelled);

        var result = await IsCompletedAsync(tenantId, reservationId);

        result.Should().BeFalse();
    }

    // ---- Helpers ----

    private enum CleaningState
    {
        Pending,
        Completed,
        Cancelled,
    }

    private async Task<bool> IsCompletedAsync(Guid tenantId, Guid reservationId)
    {
        await using var dbContext = CreateAppDbContext(tenantId);
        var reader = new CleaningReadinessReader(dbContext, NullLogger<CleaningReadinessReader>.Instance);
        return await reader.IsCleaningCompletedAsync(tenantId, reservationId, CancellationToken.None);
    }

    private async Task SeedCleaningAsync(Guid tenantId, Guid reservationId, CleaningState state)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateAppDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), reservationId, createdByUserId: null, now);

        switch (state)
        {
            case CleaningState.Completed:
                cleaning.Assign(Guid.NewGuid(), now);
                cleaning.Start(now);
                cleaning.StartInspection(now);
                cleaning.Complete(now);
                break;
            case CleaningState.Cancelled:
                cleaning.Cancel(now);
                break;
        }

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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
