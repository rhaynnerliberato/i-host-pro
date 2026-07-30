using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Login/refresh/logout HTTP endpoints (Incremento 2 plan, Etapa 14). Every
/// action only builds a Command from already-validated/captured input and
/// dispatches it through <see cref="ISender"/> — never calls a concrete
/// handler directly, never touches Infrastructure. <see cref="ResultHttpMapper"/>
/// is the single place a failed <see cref="IHostPro.BuildingBlocks.Domain.Result"/>
/// becomes an HTTP response.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;

    public AuthController(ISender sender) => _sender = sender;

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var command = new LoginCommand(
            request.TenantSlug ?? string.Empty,
            request.Email ?? string.Empty,
            request.Password ?? string.Empty,
            CaptureRequestContext());

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var command = new RefreshTokenCommand(request.RefreshToken ?? string.Empty, CaptureRequestContext());

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        // Re-validated here even though the exact same check already ran in
        // OnTokenValidated (Etapa 13) — [Authorize] only proves the token
        // signature/lifetime/issuer/audience were valid at that point, never
        // that THIS action can blindly trust User.Claims for its own
        // authorization-sensitive use (Incremento 2 plan, Etapa 14: "claims
        // de logout devem ser lidas após autenticação e validadas novamente").
        if (!AuthenticatedIdentityReader.TryRead(User, out var identity))
            return Unauthorized();

        var command = new LogoutCommand(identity.TenantId, identity.UserId, identity.SessionId);
        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? NoContent() : ResultHttpMapper.ToActionResult(result.Error);
    }

    private void SetNoStoreHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    /// <summary>
    /// IP/User-Agent are captured here — never accepted as request-body
    /// fields (Incremento 2 plan, Etapa 14). <see cref="HttpContext.Connection"/>'s
    /// <c>RemoteIpAddress</c> is the raw TCP peer address; <c>X-Forwarded-For</c>
    /// is deliberately never read here — this host has no
    /// <c>UseForwardedHeaders</c>/trusted-proxy configuration, and honoring
    /// that header without one would let any client spoof its own IP.
    /// </summary>
    private AuthenticationRequestContext CaptureRequestContext()
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Request.Headers.UserAgent.ToString();

        return new AuthenticationRequestContext(
            IpAddress: ipAddress,
            Device: null,
            Browser: string.IsNullOrEmpty(userAgent) ? null : userAgent);
    }

    private static AuthTokensResponse ToResponse(AuthTokensResult result) => new(
        result.AccessToken, result.AccessTokenExpiresAt, result.RefreshToken, result.RefreshTokenExpiresAt, result.TokenType);
}
