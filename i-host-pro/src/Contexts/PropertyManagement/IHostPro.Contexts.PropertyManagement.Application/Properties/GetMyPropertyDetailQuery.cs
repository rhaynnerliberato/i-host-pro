using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Reads a single property's detail, scoped to the authenticated owner
/// (Checkpoint 5 plan, item 10). <see cref="OwnerUserId"/> comes exclusively
/// from validated JWT claims. A property that exists but is not linked to
/// this owner, cross-tenant, or genuinely nonexistent are all
/// indistinguishable — every one returns <see cref="Errors.PropertyManagementErrorCodes.PropertyNotFound"/>.
/// </summary>
public sealed record GetMyPropertyDetailQuery(Guid OwnerUserId, Guid PropertyId) : IQuery<PropertyResult>;
