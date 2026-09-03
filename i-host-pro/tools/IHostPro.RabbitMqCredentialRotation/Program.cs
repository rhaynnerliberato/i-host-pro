using Amazon.SecretsManager;
using IHostPro.RabbitMqCredentialRotation;
using Microsoft.Extensions.Configuration;
using Serilog;

// One-off tool: rotates the Amazon MQ RabbitMQ bootstrap credential (CP5.3B
// ACCEPTED_PILOT_SECURITY_EXCEPTION) via RabbitMQ's own Management HTTP API.
// See RabbitMqCredentialRotator.cs for the rotation logic itself and its
// failure-window handling.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "IHostPro.RabbitMqCredentialRotation")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var rabbitMqSecretArn = RequireConfig(configuration, "RabbitMqCredentialRotation:RabbitMqSecretArn");
    // Default "Rotate" (unchanged behavior) - "Verify" runs a read-only
    // check confirming the current secret authenticates and the version it
    // superseded no longer does, without rotating again. Selected via a
    // container command/environment override at RunTask time, not a
    // separate image/task definition.
    var mode = configuration["RabbitMqCredentialRotation:Mode"] ?? "Rotate";

    using var secretsClient = new AmazonSecretsManagerClient();
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var secretsManagerClient = new AwsSecretsManagerClient(secretsClient);

    var rotator = new RabbitMqCredentialRotator(httpClient, secretsManagerClient);

    if (string.Equals(mode, "Verify", StringComparison.OrdinalIgnoreCase))
    {
        var (finalCredentialAccepted, bootstrapCredentialRejected) = await rotator.VerifyRotationAsync(rabbitMqSecretArn);
        return finalCredentialAccepted && bootstrapCredentialRejected ? 0 : 1;
    }

    await rotator.RotateAsync(rabbitMqSecretArn);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "RabbitMQ credential rotation failed.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string RequireConfig(IConfiguration configuration, string key)
{
    var value = configuration[key];
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Missing required configuration value: {key}")
        : value;
}
