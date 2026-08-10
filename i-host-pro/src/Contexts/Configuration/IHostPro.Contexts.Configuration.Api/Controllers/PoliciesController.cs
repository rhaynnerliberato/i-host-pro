using System.Text.Json;
using IHostPro.Contexts.Configuration.Api.Contracts;
using IHostPro.Contexts.Configuration.Api.Http;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Configuration.Api.Controllers;

/// <summary>
/// Administrative policy catalog/value/history/effective-value endpoints
/// (Fase 5, Incremento 1, Checkpoint 4) — mirrors <c>ReservationsController</c>
/// exactly. Read actions require <see cref="IdentityPermissionCodes.PoliciesRead"/>;
/// creating a new version requires <see cref="IdentityPermissionCodes.PoliciesManage"/>.
/// Every action reads the actor exclusively from
/// <see cref="ConfigurationIdentityReader"/> — never a request-body-supplied
/// tenant/actor id. Never exposes a way to create/alter/remove a
/// <c>PolicyDefinition</c>, physically delete a value, edit GLOBAL, restore
/// an old version, or set a vigência window (Fase 5, Incremento 1 official
/// decisions §8: explicitly out of scope for every endpoint here).
/// </summary>
[ApiController]
[Route("api/v1/policies")]
public sealed class PoliciesController : ControllerBase
{
    private readonly IConfigurationRequestDispatcher _sender;

    public PoliciesController(IConfigurationRequestDispatcher sender) => _sender = sender;

    [HttpGet]
    [Authorize(Policy = IdentityPermissionCodes.PoliciesRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PolicyDefinitionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out _))
            return Unauthorized();

        var result = await _sender.Send(new ListPolicyDefinitionsQuery(), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : PolicyResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{policyCode}/values")]
    [Authorize(Policy = IdentityPermissionCodes.PoliciesRead)]
    [ProducesResponseType(typeof(PolicyValueDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetValue(
        string policyCode, [FromQuery] string? scopeType, [FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new GetPolicyValueByScopeQuery(identity.TenantId, policyCode, scopeType ?? string.Empty, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PolicyResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{policyCode}/effective")]
    [Authorize(Policy = IdentityPermissionCodes.PoliciesRead)]
    [ProducesResponseType(typeof(EffectivePolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEffective(string policyCode, [FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(new GetEffectivePolicyQuery(identity.TenantId, policyCode, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(ToResponse(result.Value))
            : PolicyResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpGet("{policyCode}/history")]
    [Authorize(Policy = IdentityPermissionCodes.PoliciesRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PolicyValueDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(
        string policyCode, [FromQuery] string? scopeType, [FromQuery] Guid? propertyId, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var result = await _sender.Send(
            new GetPolicyHistoryQuery(identity.TenantId, policyCode, scopeType ?? string.Empty, propertyId), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value.Select(ToResponse).ToArray())
            : PolicyResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("{policyCode}/values")]
    [Authorize(Policy = IdentityPermissionCodes.PoliciesManage)]
    [ProducesResponseType(typeof(PolicyValueDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateVersion(
        string policyCode, [FromBody] CreatePolicyValueVersionRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        if (!ConfigurationIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new CreatePolicyValueVersionCommand(
            identity.TenantId,
            identity.UserId,
            policyCode,
            request.ScopeType ?? string.Empty,
            request.PropertyId,
            request.Value.GetRawText(),
            request.Reason ?? string.Empty,
            request.ExpectedVersion,
            HttpContext.Connection.RemoteIpAddress?.ToString());

        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
            return PolicyResultHttpMapper.ToActionResult(result.Error);

        var response = ToResponse(result.Value);
        return CreatedAtAction(
            nameof(GetValue), new { policyCode, scopeType = response.ScopeType, propertyId = response.PropertyId }, response);
    }

    private static PolicyDefinitionResponse ToResponse(PolicyDefinitionResult result) => new(
        result.Code, result.Name, result.Description, result.Category, result.ValueType, result.SchemaVersion, result.IsActive);

    private static PolicyValueDetailResponse ToResponse(PolicyValueDetailResult result) => new(
        result.Id, result.PolicyCode, result.ScopeType, result.ScopeReferenceId, result.Version,
        JsonSerializer.Deserialize<JsonElement>(result.Value), result.CreatedAtUtc, result.CreatedByUserId, result.Reason, result.IsCurrent);

    private static EffectivePolicyResponse ToResponse(EffectivePolicyResult result) => new(
        result.PolicyCode, result.Status.ToString(), result.Value, result.ResolvedScope?.ToString(), result.Version);

    private void SetNoStoreHeaders() => Response.Headers.CacheControl = "no-store";
}
