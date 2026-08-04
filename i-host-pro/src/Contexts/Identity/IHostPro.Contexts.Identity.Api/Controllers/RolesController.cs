using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Read-only listing of the platform's fixed role catalog (Incremento 3,
/// Checkpoint 3) — for the future administrative interface. Only builds a
/// Query from the request and dispatches it through <see cref="IIdentityRequestDispatcher"/>,
/// never touches <c>IdentityDbContext</c>, <c>IdentityCatalogSeed</c> or any
/// other Infrastructure type directly (this project does not even reference
/// <c>Identity.Infrastructure</c>). No endpoint here creates, edits or
/// deletes a role — the catalog is platform-fixed in this phase (Documento
/// 09 §18).
/// </summary>
[ApiController]
[Route("api/v1/roles")]
public sealed class RolesController : ControllerBase
{
    private readonly IIdentityRequestDispatcher _sender;

    public RolesController(IIdentityRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.RolesRead)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        // Administrative catalog data — never cached by an intermediary
        // (Incremento 3, Checkpoint 3, explicit requirement).
        Response.Headers.CacheControl = "no-store";

        var result = await _sender.Send(new ListRolesQuery(), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : ResultHttpMapper.ToActionResult(result.Error);
    }

    private static RoleResponse ToResponse(CatalogRole role) => new(role.Code, role.Name, role.PermissionCodes);
}
