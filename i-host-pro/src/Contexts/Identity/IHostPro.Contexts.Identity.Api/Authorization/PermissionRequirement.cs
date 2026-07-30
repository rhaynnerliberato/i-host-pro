using Microsoft.AspNetCore.Authorization;

namespace IHostPro.Contexts.Identity.Api.Authorization;

/// <summary>
/// An ASP.NET Core authorization requirement satisfied when the current
/// principal's role(s) grant <see cref="PermissionCode"/>, resolved from the
/// platform's fixed Role → Permission catalog (Documento 09; RBAC decided in
/// ADR-005/ADR-012). Declared here, in <c>Identity.Api</c>, because
/// <see cref="IAuthorizationRequirement"/> is an ASP.NET Core type — neither
/// <c>Identity.Application</c> nor <c>Identity.Infrastructure</c> may
/// reference it (Incremento 3 plan, Checkpoint 1, approved layering).
///
/// The handler that evaluates this requirement (<c>PermissionAuthorizationHandler</c>)
/// is introduced in Checkpoint 2, together with the permission resolver it
/// depends on — registering this requirement and its policies now (Checkpoint
/// 1) without a handler is safe: ASP.NET Core authorization fails closed
/// (denies) when no registered handler succeeds a requirement, and no
/// endpoint references any of these policies yet.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string PermissionCode { get; }

    public PermissionRequirement(string permissionCode)
    {
        if (string.IsNullOrWhiteSpace(permissionCode))
            throw new ArgumentException("Permission code cannot be empty.", nameof(permissionCode));

        PermissionCode = permissionCode;
    }
}
