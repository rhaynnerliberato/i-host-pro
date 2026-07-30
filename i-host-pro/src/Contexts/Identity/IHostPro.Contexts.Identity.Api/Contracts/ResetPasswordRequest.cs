namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Deliberately declares no <c>tenantId</c>/<c>actorId</c>/<c>currentPassword</c>/
/// <c>email</c> field — the actor/tenant come exclusively from
/// <see cref="IHostPro.Contexts.Identity.Api.Http.AuthenticatedIdentityReader"/>,
/// and no current password is required for an administrative reset
/// (Incremento 3, Checkpoint 9).
/// </summary>
public sealed record ResetPasswordRequest(string? NewPassword);
