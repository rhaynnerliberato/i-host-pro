using Microsoft.Extensions.Configuration;

namespace IHostPro.HomologScenarioProvisioning;

public static class ScenarioConfiguration
{
    public static string RequireConfig(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing required configuration value: {key}")
            : value;
    }
}
