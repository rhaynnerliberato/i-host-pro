using IHostPro.Contexts.Configuration.Api.Contracts;
using IHostPro.Contexts.Configuration.Api.Http;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Templates;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Configuration.Api.Controllers;

/// <summary>
/// Administrative Template CRUD endpoints (Fase 9, Checkpoint 1 —
/// "Comunicação e Integrações do MVP") — mirrors <c>PoliciesController</c>
/// exactly. Read actions require <see cref="IdentityPermissionCodes.TemplatesRead"/>;
/// writes require <see cref="IdentityPermissionCodes.TemplatesManage"/> —
/// both already seeded (ADMIN=manage, AI_AGENT=read), never a newly-invented
/// permission. No version history, no rollback, no visual editor — exactly
/// the MVP the Fase 9 doc registers. Every action reads the actor
/// exclusively from <see cref="ConfigurationIdentityReader"/> — never a
/// request-body-supplied tenant id.
/// </summary>
[ApiController]
[Route("api/v1/templates")]
public sealed class TemplatesController : ControllerBase
{
    private readonly IConfigurationRequestDispatcher _sender;

    public TemplatesController(IConfigurationRequestDispatcher sender) => _sender = sender;

    [HttpGet("{key}")]
    [Authorize(Policy = IdentityPermissionCodes.TemplatesRead)]
    [ProducesResponseType(typeof(TemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByKey(string key, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new GetTemplateByKeyQuery(key), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : TemplateResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost]
    [Authorize(Policy = IdentityPermissionCodes.TemplatesManage)]
    [ProducesResponseType(typeof(TemplateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateTemplateRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new CreateTemplateCommand(identity.TenantId, request.Key, request.Content), cancellationToken);

        if (!result.IsSuccess)
            return TemplateResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(nameof(GetByKey), new { key = response.Key }, response);
    }

    [HttpPut("{key}")]
    [Authorize(Policy = IdentityPermissionCodes.TemplatesManage)]
    [ProducesResponseType(typeof(TemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateContent(
        string key, [FromBody] UpdateTemplateContentRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new UpdateTemplateContentCommand(key, request.Content), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : TemplateResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{key}/activate")]
    [Authorize(Policy = IdentityPermissionCodes.TemplatesManage)]
    [ProducesResponseType(typeof(TemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Activate(string key, CancellationToken cancellationToken) =>
        SetActive(key, isActive: true, cancellationToken);

    [HttpPost("{key}/deactivate")]
    [Authorize(Policy = IdentityPermissionCodes.TemplatesManage)]
    [ProducesResponseType(typeof(TemplateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Deactivate(string key, CancellationToken cancellationToken) =>
        SetActive(key, isActive: false, cancellationToken);

    private async Task<IActionResult> SetActive(string key, bool isActive, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new SetTemplateActiveCommand(key, isActive), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : TemplateResultHttpMapper.ToActionResult(result.Error);
    }

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";

    private static TemplateResponse ToResponse(TemplateResult result) => new(
        result.Id, result.Key, result.Content, result.IsActive, result.CreatedAtUtc, result.UpdatedAtUtc);
}
