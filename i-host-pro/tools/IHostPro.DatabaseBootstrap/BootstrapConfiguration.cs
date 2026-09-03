using Microsoft.Extensions.Configuration;

namespace IHostPro.DatabaseBootstrap;

public static class BootstrapConfiguration
{
    // Baked into every image by docker/{Api,Worker,MigrationRunner,
    // DatabaseBootstrap}.Dockerfile from the official AWS RDS global trust
    // bundle (covers CA rotation automatically - see the Dockerfile
    // comments). Never a per-instance CA identifier (rds-ca-rsa2048-g1
    // today), which would need updating on every future AWS CA rotation.
    public const string RdsCaBundlePath = "/app/rds-ca/global-bundle.pem";

    public static string RequireConfig(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        // appsettings.json ships with these keys present but empty
        // (documenting the expected shape without a real value baked into
        // the image) - an empty string must fail exactly like a missing
        // key, not silently reach the AWS SDK and fail there instead with a
        // much less clear error.
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing required configuration value: {key}")
            : value;
    }
}
