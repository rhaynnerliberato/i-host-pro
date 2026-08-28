using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Infrastructure.Communication;

/// <inheritdoc cref="IPixChargeDeliveryReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IPixChargeDeliveryReader"/> (Fase 10, Checkpoint 5 — ADR-027)
/// — lives in <c>Payments.Infrastructure</c>, the one layer allowed to touch
/// <see cref="PaymentsDbContext"/> directly. Opens its own short-lived,
/// read-only, tenant-scoped transaction via <see cref="TenantAwareTransactionScope"/>
/// using a throwaway local <see cref="TenantContext"/> set to the
/// caller-supplied <paramref name="tenantId"/> — mirrors
/// <c>PropertyManagement.Infrastructure.Communication.FrontDeskContactReader"/>'s
/// own reasoning exactly (ADR-026's structural precedent).
///
/// Never re-invokes <c>IPixProvider</c> — the persisted
/// <see cref="Domain.PixCharge.QrCodePayload"/> is the single source of
/// truth once a charge has been accepted (explicit product decision — see
/// ADR-025).
/// </remarks>
public sealed class PixChargeDeliveryReader : IPixChargeDeliveryReader
{
    private readonly PaymentsDbContext _dbContext;

    public PixChargeDeliveryReader(PaymentsDbContext dbContext) => _dbContext = dbContext;

    public async Task<PixChargeDeliveryReadResult?> GetForDeliveryAsync(
        Guid tenantId, Guid pixChargeId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var charge = await _dbContext.PixCharges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == pixChargeId, cancellationToken);

        if (charge?.QrCodePayload is null)
            return null;

        return new PixChargeDeliveryReadResult(charge.Id, charge.QrCodePayload, charge.Amount, charge.CurrencyCode, charge.ExpiresAtUtc);
    }
}
