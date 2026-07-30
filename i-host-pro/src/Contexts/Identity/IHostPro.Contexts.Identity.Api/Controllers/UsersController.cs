using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application.Profile;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Self-service profile/session endpoints (Incremento 3, Checkpoint 4) — the
/// authenticated caller acting only on their own resources, never on another
/// user's. Every action reads <see cref="AuthenticatedIdentityReader"/>
/// (never a route/body-supplied user or tenant id) and only builds a
/// Query/Command from it before dispatching through <see cref="ISender"/> —
/// never touches Infrastructure. <c>[Authorize]</c> alone (no policy):
/// unlike <c>RolesController</c>/<c>PermissionsController</c>, these are
/// operations on the caller's own resource, not permission-gated
/// administrative actions.
/// </summary>
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly ISender _sender;

    public UsersController(ISender sender) => _sender = sender;

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetOwnProfile(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetOwnProfileQuery(identity.UserId), cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("me/sessions")]
    [Authorize]
    public async Task<IActionResult> ListOwnSessions(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new ListOwnSessionsQuery(identity.UserId, identity.SessionId), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpDelete("me/sessions/{sessionId:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeOwnSession(Guid sessionId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RevokeOwnSessionCommand(identity.TenantId, identity.UserId, sessionId);
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("me/change-password")]
    [Authorize]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new ChangeOwnPasswordCommand(
            identity.TenantId, identity.UserId, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static OwnProfileResponse ToResponse(OwnProfileResult result) => new(
        result.Id, result.FullName, result.Email, result.Status, result.Roles, result.CreatedAt, result.LastLoginAt);

    private static OwnSessionResponse ToResponse(OwnSessionResult result) => new(
        result.SessionId, result.CreatedAt, result.LastActivityAt, result.IsCurrent, result.Browser);
}
