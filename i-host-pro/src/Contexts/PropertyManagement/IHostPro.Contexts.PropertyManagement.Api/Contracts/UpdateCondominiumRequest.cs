namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// Both fields are individually optional — <c>null</c>/omitted means "leave
/// unchanged"; at least one must be supplied (Checkpoint 2 plan, item 7).
/// Deliberately declares no <c>tenantId</c>/<c>actorId</c>/<c>updatedBy</c>
/// field — the actor/tenant come exclusively from
/// <see cref="IHostPro.Contexts.PropertyManagement.Api.Http.PropertyManagementIdentityReader"/>.
/// When <see cref="Address"/> is supplied, it always replaces the whole
/// address — never a partial patch of individual address fields.
/// </summary>
public sealed record UpdateCondominiumRequest(string? Name, AddressRequest? Address);
