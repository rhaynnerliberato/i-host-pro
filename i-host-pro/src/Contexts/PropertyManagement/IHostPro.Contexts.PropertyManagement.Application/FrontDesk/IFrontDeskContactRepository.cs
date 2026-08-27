using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

public interface IFrontDeskContactRepository : IRepository<FrontDeskContact, Guid>
{
    /// <summary>
    /// A trackable lookup by <c>CondominiumId</c> (not <c>Id</c>) — needed
    /// because <see cref="SetFrontDeskContactCommandHandler"/> upserts by
    /// Condominium, mirrors <c>IMessageRepository.GetByIdempotencyKeyAsync</c>'s
    /// own "repository lookup by a non-Id key" precedent. Tenant-scoped
    /// implicitly (the Global Query Filter), same as every other repository
    /// in this Bounded Context.
    /// </summary>
    Task<FrontDeskContact?> GetByCondominiumIdAsync(Guid condominiumId, CancellationToken cancellationToken);
}
