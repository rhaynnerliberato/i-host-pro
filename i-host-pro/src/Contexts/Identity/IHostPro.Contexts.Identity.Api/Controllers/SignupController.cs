using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Public self-service signup (Self-Service Identity &amp; Onboarding
/// Foundation gate) — a separate controller/route from <see cref="AuthController"/>
/// since this is a distinct public surface (creates a new tenant, never
/// authenticates against an existing one). Calls <see cref="ISignupProcessor"/>
/// DIRECTLY, bypassing <see cref="IIdentityRequestDispatcher"/> — see that
/// interface's own remarks for why.
/// </summary>
[ApiController]
[Route("api/v1/signup")]
public sealed class SignupController : ControllerBase
{
    private readonly ISignupProcessor _signupProcessor;

    public SignupController(ISignupProcessor signupProcessor) => _signupProcessor = signupProcessor;

    [HttpPost]
    [AllowAnonymous]
    // Same policy/rationale as AuthController.Login — a public, unauthenticated,
    // brute-forceable/spammable endpoint.
    [EnableRateLimiting("Authentication")]
    [ProducesResponseType(typeof(SignupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";

        var result = await _signupProcessor.ProcessAsync(
            request.CompanyName ?? string.Empty,
            request.AdminFullName ?? string.Empty,
            request.AdminEmail ?? string.Empty,
            request.Password ?? string.Empty,
            cancellationToken);

        if (!result.IsSuccess)
            return ResultHttpMapper.ToActionResult(result.Error);

        var tokens = result.Value.Tokens;
        return Ok(new SignupResponse(
            result.Value.TenantSlug,
            new AuthTokensResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt, tokens.TokenType)));
    }
}
