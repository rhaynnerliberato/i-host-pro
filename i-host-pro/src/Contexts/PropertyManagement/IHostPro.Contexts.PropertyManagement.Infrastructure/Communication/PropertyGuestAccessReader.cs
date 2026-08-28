using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Communication;

/// <inheritdoc cref="IPropertyGuestAccessReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IPropertyGuestAccessReader"/> (Fase 10, Checkpoint 6.2 —
/// ADR-028, synchronous exception #12) — lives in
/// <c>PropertyManagement.Infrastructure</c>, the one layer allowed to touch
/// <see cref="PropertyManagementDbContext"/> directly. Opens its own
/// short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> using a throwaway local
/// <see cref="TenantContext"/> set to the caller-supplied
/// <paramref name="tenantId"/> — mirrors <see cref="FrontDeskContactReader"/>'s
/// own reasoning exactly (ADR-014's structural precedent).
///
/// <see cref="IPropertyAccessCredentialProvider"/> resolution happens
/// entirely inside this method — the resolved credential value never
/// crosses back through <see cref="PropertyAccessConfiguration"/>/EF Core,
/// it exists only in the returned <see cref="PropertyGuestAccessReadResult"/>,
/// in memory, for the caller's immediate use.
/// </remarks>
public sealed class PropertyGuestAccessReader : IPropertyGuestAccessReader
{
    private readonly PropertyManagementDbContext _dbContext;
    private readonly IPropertyAccessCredentialProvider _credentialProvider;

    public PropertyGuestAccessReader(PropertyManagementDbContext dbContext, IPropertyAccessCredentialProvider credentialProvider)
    {
        _dbContext = dbContext;
        _credentialProvider = credentialProvider;
    }

    public async Task<PropertyGuestAccessReadResult?> GetForGuestAccessDeliveryAsync(
        Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var configuration = await _dbContext.Set<PropertyAccessConfiguration>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId && c.IsActive, cancellationToken);

        if (configuration is null)
            return null;

        string? accessCredential = null;
        if (configuration.AccessCredentialSecretReference is { } reference)
        {
            accessCredential = await _credentialProvider.GetSecretAsync(reference, cancellationToken);

            if (accessCredential is null)
            {
                throw new InvalidOperationException(
                    $"PropertyAccessConfiguration {configuration.Id} references an access credential secret " +
                    $"('{reference}') that could not be resolved — this is a configuration/infrastructure " +
                    "failure, never a silent skip (CP6.2 mandate item 24).");
            }
        }

        return new PropertyGuestAccessReadResult(accessCredential, configuration.AccessInstructions);
    }
}
