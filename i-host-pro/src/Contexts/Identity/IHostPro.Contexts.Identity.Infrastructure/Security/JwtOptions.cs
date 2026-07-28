namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Access token issuance/validation parameters (ADR-012). Signing key material
/// is handled separately by <c>IJwtSigningKeyProvider</c> (not implemented yet) —
/// it is not part of this class, since no consumer of key-storage configuration
/// exists in this step. Defaults are safe starting points, not a fixed business
/// decision — every value can be overridden per environment via configuration,
/// within the bounds enforced by <see cref="JwtOptionsValidator"/>.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Identity:Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(60);
}
