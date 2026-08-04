using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// The two read-only administrative property queries (Checkpoint 3 plan,
/// item 8/9) — mirrors <c>ICondominiumReader</c>'s shape.
/// </summary>
public interface IPropertyReader
{
    /// <summary>
    /// <paramref name="pageSize"/> is defensively clamped to the fixed
    /// maximum (100) by the implementation itself — never trusts the caller
    /// alone. Never fetches own/effective address — <see cref="PropertySummaryResult"/>
    /// does not carry either (Checkpoint 3 plan, item 9: "listagem nunca
    /// retorna endereço próprio ou efetivo"). Ordered deterministically by
    /// normalized code then id (Checkpoint 3 plan, item 9).
    /// </summary>
    Task<PagedResult<PropertySummaryResult>> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the full detail, including the effective address, via
    /// projection/join rather than a per-item follow-up query — a single
    /// detail lookup is not an N+1 concern the way a per-row listing lookup
    /// would be (Checkpoint 3 plan, item 9).
    /// </summary>
    Task<PropertyResult?> GetByIdAsync(Guid propertyId, CancellationToken cancellationToken);

    /// <summary>
    /// The authenticated owner's own properties, filtered by
    /// <paramref name="ownerUserId"/> against <c>property_owners</c> —
    /// includes every status (Checkpoint 5 plan, item 2/10: Ownership never
    /// hides a linked property based on its lifecycle status). The ABAC
    /// filter lives here, in the reader/Query, never only in the controller
    /// (item 10). Same ordering/no-N+1/address-omission guarantees as
    /// <see cref="ListAsync"/>.
    /// </summary>
    Task<PagedResult<PropertySummaryResult>> ListMineAsync(Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// The authenticated owner's own property detail — filters simultaneously
    /// by tenant (Global Query Filter), <paramref name="propertyId"/> AND
    /// <paramref name="ownerUserId"/> in the SAME query (item 10); a property
    /// that exists but is not linked to this owner is indistinguishable from
    /// one that does not exist at all (both <c>null</c>). Same effective-address
    /// resolution as <see cref="GetByIdAsync"/>.
    /// </summary>
    Task<PropertyResult?> GetMineDetailAsync(Guid ownerUserId, Guid propertyId, CancellationToken cancellationToken);
}
