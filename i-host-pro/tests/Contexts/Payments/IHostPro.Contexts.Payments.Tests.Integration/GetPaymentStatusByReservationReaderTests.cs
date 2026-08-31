using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Tests.Integration;

/// <summary>
/// Real-Postgres proof for <see cref="PixChargeReader.GetStatusByReservationIdAsync"/>
/// (Fase 11, Checkpoint 3 — AI Agent's own <c>GetPaymentStatus</c> Read
/// Tool). Mirrors <c>PixChargeDeliveryReaderTests</c>' own reader-test
/// structure exactly.
/// </summary>
public class GetPaymentStatusByReservationReaderTests : IClassFixture<PaymentsFoundationTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public GetPaymentStatusByReservationReaderTests(PaymentsFoundationTests.Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    [Fact]
    public async Task Returns_null_when_no_charge_exists_for_the_reservation()
    {
        var result = await ResolveAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_the_single_charges_status_verbatim()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedChargeAsync(tenantId, reservationId, DateTimeOffset.UtcNow, c => c.Fail(DateTimeOffset.UtcNow));

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Failed");
        result.Amount.Should().Be(100m);
        result.CurrencyCode.Should().Be("BRL");
    }

    [Fact]
    public async Task Multiple_charges_resolve_to_the_most_recent_by_CreatedAtUtc_never_by_status()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var earlier = DateTimeOffset.UtcNow.AddHours(-2);
        var later = DateTimeOffset.UtcNow.AddHours(-1);

        // The EARLIER charge ends up Confirmed (the "best" status) — the
        // tie-break must still pick the LATER charge, proving status is
        // never used as a priority signal (mandate item 16).
        await SeedChargeAsync(tenantId, reservationId, earlier, c => c.Confirm(DateTimeOffset.UtcNow));
        var (_, laterChargeId) = await SeedChargeAsync(tenantId, reservationId, later, c => c.Fail(DateTimeOffset.UtcNow));

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Failed", "the most recent charge by CreatedAtUtc wins, regardless of the older charge's Confirmed status");
        _ = laterChargeId;
    }

    [Fact]
    public async Task Wrong_tenant_never_resolves_another_tenants_charge()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedChargeAsync(tenantId, reservationId, DateTimeOffset.UtcNow, c => { });

        var result = await ResolveAsync(Guid.NewGuid(), reservationId);

        result.Should().BeNull();
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid ChargeId)> SeedChargeAsync(
        Guid tenantId, Guid reservationId, DateTimeOffset createdAtUtc, Action<PixCharge> mutate)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var charge = PixCharge.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), reservationId, 100m, "BRL", createdAtUtc);
        mutate(charge);
        dbContext.PixCharges.Add(charge);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, charge.Id);
    }

    private async Task<PaymentStatusResult?> ResolveAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);
        var reader = new PixChargeReader(dbContext);

        return await reader.GetStatusByReservationIdAsync(reservationId, CancellationToken.None);
    }

    private static async Task SetTenantAsync(PaymentsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static PaymentsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "payments"))
            .Options;

        return new PaymentsDbContext(options, tenantContext);
    }
}
