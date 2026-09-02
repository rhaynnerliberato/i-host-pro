using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Administrative user creation/listing/detail (Incremento 3, Checkpoint 5),
/// role assignment/removal (Checkpoint 6), block/unblock (Checkpoint 7),
/// name/e-mail update (Checkpoint 8) and password reset (Checkpoint 9) —
/// every action requires <see cref="IdentityPermissionCodes.UsersManage"/>, unlike
/// <see cref="UsersController"/>'s self-service, own-resource-only actions
/// (no policy). A separate controller class, deliberately: mixing
/// administrative and self-service actions in one class would blur that
/// distinction. Every action reads the actor exclusively from
/// <see cref="AuthenticatedIdentityReader"/> — never a request-body-supplied
/// actor/tenant id — and only builds a Query/Command before dispatching
/// through <see cref="IIdentityRequestDispatcher"/>; never touches Infrastructure.
/// </summary>
[ApiController]
[Route("api/v1/users")]
// Fase 12, Checkpoint 3 — every action here requires UsersManage, so this
// whole controller is the platform's "Administrative API" HTTP category
// (Decision Gate §2), partitioned by TenantId+UserId (see
// ApiRateLimitingExtensions, IHostPro.Api's composition root) instead of the
// broader default TenantApi policy every other controller gets.
[EnableRateLimiting("AdminApi")]
public sealed class UserAdministrationController : ControllerBase
{
    private readonly IIdentityRequestDispatcher _sender;

    public UserAdministrationController(IIdentityRequestDispatcher sender) => _sender = sender;

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreateUserCommand(
            identity.TenantId,
            identity.UserId,
            request.FullName ?? string.Empty,
            request.Email ?? string.Empty,
            request.InitialPassword ?? string.Empty,
            request.RoleCode ?? string.Empty);

        var result = await _sender.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return ResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(GetById), new { userId = response.Id }, response);
    }

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(PagedUserResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? search,
        [FromQuery] UserStatus? status,
        CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListUsersQuery(page, pageSize, search, status), cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{userId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid userId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetUserByIdQuery(userId), cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPatch("{userId:guid}")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid userId, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UpdateUserCommand(identity.TenantId, identity.UserId, userId, request.FullName, request.Email);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{userId:guid}/roles")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignRole(Guid userId, [FromBody] AssignRoleRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new AssignRoleCommand(identity.TenantId, identity.UserId, userId, request.RoleCode ?? string.Empty);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpDelete("{userId:guid}/roles/{roleCode}")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveRole(Guid userId, string roleCode, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new RemoveRoleCommand(identity.TenantId, identity.UserId, userId, roleCode);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{userId:guid}/block")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Block(Guid userId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new BlockUserCommand(identity.TenantId, identity.UserId, userId);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{userId:guid}/unblock")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new UnblockUserCommand(identity.TenantId, identity.UserId, userId);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{userId:guid}/reset-password")]
    [Authorize(Policy = IdentityPermissionCodes.UsersManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetPassword(Guid userId, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new AdminResetPasswordCommand(identity.TenantId, identity.UserId, userId, request.NewPassword ?? string.Empty);

        var result = await _sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static UserResponse ToResponse(UserResult result) => new(
        result.Id, result.FullName, result.Email, result.Status, result.Roles, result.CreatedAt, result.LastLoginAt);

    private static PagedUserResponse ToResponse(PagedResult<UserResult> result) => new(
        result.Page, result.PageSize, result.TotalCount, result.Items.Select(ToResponse).ToArray());
}
