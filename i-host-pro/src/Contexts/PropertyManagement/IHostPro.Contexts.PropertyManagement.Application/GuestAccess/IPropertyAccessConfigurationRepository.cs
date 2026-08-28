using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

public interface IPropertyAccessConfigurationRepository : IRepository<PropertyAccessConfiguration, Guid>
{
    /// <summary>
    /// A trackable lookup by <c>PropertyId</c> (not <c>Id</c>) — needed
    /// because <see cref="SetPropertyAccessConfigurationCommandHandler"/>
    /// upserts by Property, mirrors <c>IFrontDeskContactRepository.GetByCondominiumIdAsync</c>'s
    /// own "repository lookup by a non-Id key" precedent. Tenant-scoped
    /// implicitly (the Global Query Filter), same as every other repository
    /// in this Bounded Context.
    /// </summary>
    Task<PropertyAccessConfiguration?> GetByPropertyIdAsync(Guid propertyId, CancellationToken cancellationToken);
}
