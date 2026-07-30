using IHostPro.Contexts.Identity.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Identity.Api.Authorization;

/// <summary>
/// Evaluates a <see cref="PermissionRequirement"/> against the current
/// principal's <c>role</c> claim(s), resolved to granted permission codes via
/// <see cref="IPermissionReader"/> (Incremento 3, Checkpoint 2). The only
/// <see cref="IAuthorizationHandler"/> in the solution, deliberately confined
/// to <c>Identity.Api</c> — neither <c>Identity.Application</c> nor
/// <c>Identity.Infrastructure</c> may reference the ASP.NET Core
/// authorization framework (Checkpoint 1, approved layering).
///
/// Reads the <c>role</c> claim exactly as <c>JwtTokenGenerator</c> writes it
/// and <c>ConfigureJwtBearerOptions</c>/<c>AuthController</c> already read it
/// elsewhere: <see cref="AuthorizationHandlerContext.User"/>'s claims of type
/// <c>"role"</c>, one <see cref="System.Security.Claims.Claim"/> per role code
/// (a JSON array in the token payload becomes multiple same-type claims once
/// validated — confirmed against the real token generator and JWT Bearer
/// pipeline, not assumed; see <c>JwtTokenGeneratorTests.Generated_token_supports_multiple_roles</c>
/// and <c>JwtBearerAuthenticationTests.A_token_with_multiple_roles_exposes_all_of_them_via_IsInRole</c>).
/// Never a comma-separated list, never any other alternative format.
///
/// This handler never writes to PostgreSQL, never publishes an Integration
/// Event, never touches <c>ITenantContext</c>, and never logs a token,
/// e-mail or other sensitive value — only the stable, non-sensitive
/// permission code being checked, at most, for a normal denial. It
/// deliberately does not catch exceptions from <see cref="IPermissionReader"/>:
/// an infrastructure failure (e.g. PostgreSQL unreachable) must propagate as
/// an unhandled exception — surfacing as the project's existing default 5xx
/// behavior — never be silently converted into an ordinary "permission
/// denied" (Incremento 3, Checkpoint 2, approved design).
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private const string RoleClaimType = "role";

    private readonly IPermissionReader _permissionReader;
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    public PermissionAuthorizationHandler(IPermissionReader permissionReader, ILogger<PermissionAuthorizationHandler> logger)
    {
        _permissionReader = permissionReader;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            LogDenied(requirement.PermissionCode, "the user is not authenticated");
            return;
        }

        // One Claim per role code (see class remarks) — never split on a
        // delimiter, never any other parsing strategy. Duplicates and
        // empty/whitespace values are removed; comparison is ordinal
        // (Incremento 3, Checkpoint 2, approved design: canonical codes only).
        var roleCodes = context.User.Claims
            .Where(claim => claim.Type == RoleClaimType)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (roleCodes.Length == 0)
        {
            LogDenied(requirement.PermissionCode, "no role claim is present");
            return;
        }

        // No CancellationToken is available from AuthorizationHandlerContext
        // — not an omission; ASP.NET Core's authorization handler contract
        // does not carry one (Incremento 3, Checkpoint 2, approved design).
        var grantedPermissionCodes = await _permissionReader.GetPermissionCodesAsync(roleCodes, CancellationToken.None);

        if (grantedPermissionCodes.Contains(requirement.PermissionCode, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
            return;
        }

        LogDenied(requirement.PermissionCode, "none of the user's roles grant it");
    }

    private void LogDenied(string permissionCode, string reason) =>
        _logger.LogDebug("Authorization denied for permission {PermissionCode}: {Reason}.", permissionCode, reason);
}
