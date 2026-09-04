using Microsoft.Extensions.Configuration;

namespace IHostPro.TenantProvisioning;

public static class ProvisioningConfiguration
{
    // Baked into docker/TenantProvisioning.Dockerfile from the official AWS
    // RDS global trust bundle — same convention as every other tool that
    // connects to RDS (Api/Worker/MigrationRunner/DatabaseBootstrap).
    public const string RdsCaBundlePath = "/app/rds-ca/global-bundle.pem";

    public static string RequireConfig(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        // appsettings.json ships with these keys present but empty
        // (documenting the expected shape without a real value baked into
        // the image) - an empty string must fail exactly like a missing
        // key, not silently reach the AWS SDK/EF Core and fail there
        // instead with a much less clear error.
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing required configuration value: {key}")
            : value;
    }
}
