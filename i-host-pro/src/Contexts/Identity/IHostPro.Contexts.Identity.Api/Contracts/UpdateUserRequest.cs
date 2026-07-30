namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Both fields are individually optional — <c>null</c>/omitted means "leave
/// unchanged"; at least one must be supplied (Incremento 3, Checkpoint 8).
/// Deliberately declares no <c>tenantId</c>/<c>actorId</c>/<c>updatedBy</c>/
/// <c>roles</c>/<c>status</c>/<c>password</c> field — the actor/tenant come
/// exclusively from <see cref="IHostPro.Contexts.Identity.Api.Controllers.AuthenticatedIdentityReader"/>.
/// </summary>
public sealed record UpdateUserRequest(string? FullName, string? Email);
