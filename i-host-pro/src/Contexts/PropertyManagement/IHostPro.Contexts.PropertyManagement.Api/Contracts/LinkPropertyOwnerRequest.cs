namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// The only client-supplied field for <c>POST /api/v1/properties/{id}/owners</c>
/// (Checkpoint 5 plan, item 7) — nullable at the wire level, presence
/// validated by the Application layer, never here. Never accepts
/// <c>tenantId</c>/<c>actorId</c>/<c>requiredRoleCode</c>/<c>role</c>/
/// <c>isActive</c>/<c>createdBy</c>.
/// </summary>
public sealed record LinkPropertyOwnerRequest(Guid? OwnerUserId);
