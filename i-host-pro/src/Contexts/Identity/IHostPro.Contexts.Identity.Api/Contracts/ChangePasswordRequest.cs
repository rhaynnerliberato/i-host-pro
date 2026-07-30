namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Deliberately declares no <c>tenantId</c>/<c>actorId</c>/<c>userId</c>/
/// <c>email</c> field — the actor/tenant come exclusively from
/// <see cref="IHostPro.Contexts.Identity.Api.Http.AuthenticatedIdentityReader"/>
/// (Incremento 3, Checkpoint 9).
/// </summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
