using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Communication;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Tests.Integration;

/// <summary>
/// Real-Postgres proof for <see cref="PixChargeDeliveryReader"/> (Fase 10,
/// Checkpoint 5 — ADR-027, synchronous exception #11). Mirrors
/// <c>FrontDeskContactTests</c>' own reader-test structure exactly.
/// </summary>
public class PixChargeDeliveryReaderTests : IClassFixture<PaymentsFoundationTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PixChargeDeliveryReaderTests(PaymentsFoundationTests.Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    [Fact]
    public async Task Returns_the_persisted_QrCodePayload_for_an_accepted_charge()
    {
        var (tenantId, chargeId) = await SeedAcceptedChargeAsync();

        var result = await ResolveAsync(tenantId, chargeId);

        result.Should().NotBeNull();
        result!.PixChargeId.Should().Be(chargeId);
        result.QrCodePayload.Should().Be("00020126FAKEQR");
        result.Amount.Should().Be(100m);
        result.CurrencyCode.Should().Be("BRL");
    }

    [Fact]
    public async Task Returns_null_for_a_charge_with_no_provider_acceptance_yet()
    {
        var tenantId = Guid.NewGuid();
        var chargeId = await SeedPendingUnacceptedChargeAsync(tenantId);

        var result = await ResolveAsync(tenantId, chargeId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_for_an_unknown_charge_id()
    {
        var result = await ResolveAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Wrong_tenant_never_resolves_another_tenants_charge()
    {
        var (_, chargeId) = await SeedAcceptedChargeAsync();

        var result = await ResolveAsync(Guid.NewGuid(), chargeId);

        result.Should().BeNull();
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid ChargeId)> SeedAcceptedChargeAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var charge = PixCharge.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), 100m, "BRL", DateTimeOffset.UtcNow);
        charge.RecordProviderAcceptance("fake-provider-123", "00020126FAKEQR", DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow);
        dbContext.PixCharges.Add(charge);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, charge.Id);
    }

    private async Task<Guid> SeedPendingUnacceptedChargeAsync(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var charge = PixCharge.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), 100m, "BRL", DateTimeOffset.UtcNow);
        dbContext.PixCharges.Add(charge);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return charge.Id;
    }

    private async Task<PixChargeDeliveryReadResult?> ResolveAsync(Guid tenantId, Guid pixChargeId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var reader = new PixChargeDeliveryReader(dbContext);

        return await reader.GetForDeliveryAsync(tenantId, pixChargeId, CancellationToken.None);
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
