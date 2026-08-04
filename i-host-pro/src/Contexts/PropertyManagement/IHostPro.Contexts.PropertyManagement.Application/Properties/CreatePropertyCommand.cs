using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Creates a new property, born in <c>PropertyStatus.Draft</c> (Checkpoint 3
/// plan, item 3). <see cref="TenantId"/>/<see cref="ActorId"/> come
/// exclusively from the authenticated Administrator's access token claims —
/// a controller builds this from <c>ClaimsPrincipal</c>, never from the
/// request body. <see cref="CondominiumId"/> and <see cref="Address"/> are
/// each independently optional, but at least one effective address source
/// must exist: <see cref="Address"/> is required only when
/// <see cref="CondominiumId"/> is <c>null</c> (Checkpoint 3 plan, item 3) —
/// enforced by the handler, since it depends on which of the two was
/// supplied, not a simple presence check either validator rule alone can
/// express.
/// </summary>
public sealed record CreatePropertyCommand(
    Guid TenantId,
    Guid ActorId,
    string Code,
    string Name,
    int Capacity,
    Guid? CondominiumId,
    PropertyAddressInput? Address) : ICommand<PropertyResult>;
