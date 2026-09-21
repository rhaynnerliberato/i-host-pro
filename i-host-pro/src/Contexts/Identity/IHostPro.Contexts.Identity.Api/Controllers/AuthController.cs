using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Http;
using IHostPro.Contexts.Identity.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IHostPro.Contexts.Identity.Api.Controllers;

/// <summary>
/// Login/refresh/logout HTTP endpoints (Incremento 2 plan, Etapa 14). Every
/// action only builds a Command from already-validated/captured input and
/// dispatches it through <see cref="IIdentityRequestDispatcher"/> — never
/// calls a concrete handler directly, never touches Infrastructure. <see cref="ResultHttpMapper"/>
/// is the single place a failed <see cref="IHostPro.BuildingBlocks.Domain.Result"/>
/// becomes an HTTP response.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IIdentityRequestDispatcher _sender;
    private readonly IStartPasswordResetProcessor _startPasswordResetProcessor;
    private readonly ICompletePasswordResetProcessor _completePasswordResetProcessor;

    public AuthController(
        IIdentityRequestDispatcher sender,
        IStartPasswordResetProcessor startPasswordResetProcessor,
        ICompletePasswordResetProcessor completePasswordResetProcessor)
    {
        _sender = sender;
        _startPasswordResetProcessor = startPasswordResetProcessor;
        _completePasswordResetProcessor = completePasswordResetProcessor;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    // Fase 12, Checkpoint 3 — brute-force/credential-stuffing protection,
    // partitioned by caller IP (see ApiRateLimitingExtensions, IHostPro.Api's
    // composition root — the literal name here is the only coupling point,
    // deliberately never a type reference: this project must not depend on
    // Infrastructure/the Host). Complements, never duplicates, the existing
    // per-user Identity lockout (AccountLockoutOptions) — that one guards a
    // single targeted account; this one guards against many accounts being
    // tried from the same IP.
    [EnableRateLimiting("Authentication")]
    [ProducesResponseType(typeof(AuthTokensResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
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
    // Fase 12, Checkpoint 3 — same Authentication policy as Login, same
    // IP partition: refresh-token abuse is explicitly in scope alongside
    // login brute force. Complements, never replaces, the independent
    // rotated-token-reuse detection RefreshTokenCommandHandler already does.
    [EnableRateLimiting("Authentication")]
    [ProducesResponseType(typeof(AuthTokensResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var command = new RefreshTokenCommand(request.RefreshToken ?? string.Empty, CaptureRequestContext());

        var result = await _sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(ToResponse(result.Value)) : ResultHttpMapper.ToActionResult(result.Error);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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

    /// <summary>
    /// Self-Service Identity &amp; Onboarding Foundation gate. Calls
    /// <see cref="IStartPasswordResetProcessor"/> DIRECTLY — bypassing
    /// <see cref="IIdentityRequestDispatcher"/>, since no trusted tenant
    /// exists yet for an anonymous request (mirrors why the Airbnb Email
    /// Bridge's OAuth callback bypasses its own dispatcher). Always returns
    /// the exact same 202 regardless of whether the tenant/account was
    /// resolved — never an account/tenant-enumeration oracle.
    /// </summary>
    [HttpPost("forgot-password/start")]
    [AllowAnonymous]
    [EnableRateLimiting("Authentication")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPasswordStart([FromBody] ForgotPasswordStartRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        await _startPasswordResetProcessor.ProcessAsync(request.TenantSlug ?? string.Empty, request.Email ?? string.Empty, cancellationToken);

        return Accepted();
    }

    /// <summary>
    /// Self-Service Identity &amp; Onboarding Foundation gate. Calls
    /// <see cref="ICompletePasswordResetProcessor"/> DIRECTLY, same reason as
    /// <see cref="ForgotPasswordStart"/>. Every failure (invalid/expired/
    /// already-consumed token) maps through the same
    /// <see cref="ResultHttpMapper"/> generic-400 branch as any other
    /// unmapped error code — never distinguished for the caller. A password-
    /// policy violation returns the same shape FluentValidation already uses
    /// elsewhere (comma-joined stable codes), since the token itself is
    /// still valid at that point (checked before consumption).
    /// </summary>
    [HttpPost("forgot-password/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("Authentication")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPasswordComplete([FromBody] ForgotPasswordCompleteRequest request, CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();

        var result = await _completePasswordResetProcessor.ProcessAsync(
            request.Token ?? string.Empty, request.NewPassword ?? string.Empty, cancellationToken);

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
