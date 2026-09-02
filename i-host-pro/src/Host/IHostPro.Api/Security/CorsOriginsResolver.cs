using Microsoft.Extensions.Configuration;

namespace IHostPro.Api.Security;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — pulled out of
/// <c>Program.cs</c> purely so the fail-fast rule below is unit-testable
/// without booting the whole host (a raw inline <c>throw</c> at the top of
/// <c>Program.cs</c> cannot be exercised in isolation: this project's own
/// top-level statements swallow unhandled exceptions into
/// <c>Log.Fatal</c>, so nothing propagates out for a test to observe). No
/// behavior change from the inline version this replaces.
/// </summary>
public static class CorsOriginsResolver
{
    public const string MissingProductionOriginsMessage =
        "Missing configuration 'Cors:AllowedOrigins' — a Production deployment must explicitly configure its " +
        "allowed frontend origin(s); there is no safe default to fall back to.";

    private static readonly string[] DevelopmentDefaultOrigins = ["http://localhost:4200"];

    /// <summary>
    /// Development/Test/every non-Production environment keeps the localhost
    /// fallback unchanged when unconfigured. Production has no safe
    /// default — a missing "Cors:AllowedOrigins" there throws rather than
    /// silently falling back to a value that would just break the real
    /// frontend with no clear signal why.
    /// </summary>
    public static string[] ResolveAllowedOrigins(IConfiguration configuration, bool isProduction)
    {
        var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        if (isProduction && (configuredOrigins is null || configuredOrigins.Length == 0))
            throw new InvalidOperationException(MissingProductionOriginsMessage);

        return configuredOrigins ?? DevelopmentDefaultOrigins;
    }
}
