using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Parses the API-facing <c>scopeType</c> string (Api never references
/// Domain, so <see cref="PolicyScopeType"/> cannot appear in any
/// Command/Query's own field) into a validated <see cref="PolicyScope"/> —
/// shared by every handler that accepts a scope, so the
/// <c>scope_not_supported</c>/<c>forbidden</c> split (Fase 5, Incremento 1
/// official decisions §8) is decided in exactly one place.
/// </summary>
internal static class PolicyScopeParser
{
    private static readonly Error ScopeNotSupportedError = new(PolicyErrorCodes.ScopeNotSupported, PolicyErrorCodes.ScopeNotSupported);
    private static readonly Error ForbiddenError = new(PolicyErrorCodes.Forbidden, PolicyErrorCodes.Forbidden);

    public static bool TryParse(string? scopeType, Guid? propertyId, out PolicyScope scope, out Error? error)
    {
        scope = null!;
        error = null;

        if (string.Equals(scopeType, "Global", StringComparison.OrdinalIgnoreCase))
        {
            error = ForbiddenError;
            return false;
        }

        if (string.Equals(scopeType, "Tenant", StringComparison.OrdinalIgnoreCase))
        {
            if (propertyId is not null)
            {
                error = ScopeNotSupportedError;
                return false;
            }

            scope = PolicyScope.Tenant();
            return true;
        }

        if (string.Equals(scopeType, "Property", StringComparison.OrdinalIgnoreCase))
        {
            if (propertyId is null || propertyId == Guid.Empty)
            {
                error = ScopeNotSupportedError;
                return false;
            }

            scope = PolicyScope.Property(propertyId.Value);
            return true;
        }

        error = ScopeNotSupportedError;
        return false;
    }
}
