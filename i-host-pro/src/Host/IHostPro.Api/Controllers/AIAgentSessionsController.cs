using IHostPro.Api.Http;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Api.Controllers;

/// <summary>
/// Manual Resume for an AI Agent session a real human handoff escalated
/// (Fase 11, Checkpoint 6 — Human Handoff, Safety &amp; Audit; CP0's own
/// <c>HumanHandoffResume=MANUAL ONLY</c> decision). No separate
/// <c>AIAgent.Api</c> project exists (CP6 mandate: <c>CreateAIAgentApiProject=false</c>)
/// — this is AI Agent's first HTTP-triggered write, hosted directly here.
/// Guarded by <see cref="IdentityPermissionCodes.AiAgentManage"/> — every
/// action reads the actor exclusively from <see cref="AIAgentIdentityReader"/>,
/// never a request-body-supplied tenant/actor id.
/// </summary>
[ApiController]
[Route("api/v1/ai-agent/sessions")]
public sealed class AIAgentSessionsController : ControllerBase
{
    private readonly IAIAgentRequestDispatcher _sender;

    public AIAgentSessionsController(IAIAgentRequestDispatcher sender) => _sender = sender;

    [HttpPost("{sessionId:guid}/resume")]
    [Authorize(Policy = IdentityPermissionCodes.AiAgentManage)]
    [ProducesResponseType(typeof(ResumeAgentSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume(Guid sessionId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!AIAgentIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new ResumeAgentSessionCommand
        {
            TenantId = identity.TenantId,
            AgentSessionId = sessionId,
            ActorId = identity.ActorId,
        };
        var result = await _sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            var value = result.Value;
            return Ok(new ResumeAgentSessionResponse(value.AgentSessionId, value.AgentHumanHandoffId, value.ResumedAtUtc));
        }

        return result.Error.Code switch
        {
            "AgentSessionNotFound" => NotFound(),
            "NoActiveHumanHandoff" => Conflict(),
            _ => Conflict(),
        };
    }
}

public sealed record ResumeAgentSessionResponse(Guid AgentSessionId, Guid AgentHumanHandoffId, DateTimeOffset ResumedAtUtc);
