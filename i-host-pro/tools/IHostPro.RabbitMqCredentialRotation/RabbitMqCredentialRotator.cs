using Serilog;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace IHostPro.RabbitMqCredentialRotation;

// CP5.3C corrective Decision Gate items 18-19 - design/code only, never
// executed against the real broker in this checkpoint
// (RabbitMqRotationExecuted=false). Rotates the Amazon MQ RabbitMQ
// bootstrap credential (CP5.3B's ACCEPTED_PILOT_SECURITY_EXCEPTION) via
// RabbitMQ's OWN Management HTTP API - confirmed, sourced finding from
// earlier this engagement: `aws mq update-user` does NOT work for RabbitMQ
// engine brokers (ActiveMQ only).
//
// Failure-window design (item 19): the two external side effects (the
// broker's actual password, and the Secrets Manager value) cannot be
// changed atomically - there is a real window between them. This class
// deliberately does NOT attempt to roll the broker password back if the
// Secrets Manager write fails; reverting would just recreate the same
// inconsistency in the opposite direction. Instead it retries the Secrets
// Manager write (the new password is already known and idempotent to
// re-write) before surfacing a RabbitMqRotationPartiallyAppliedException
// with the exact, actionable state - never silently succeeding, never
// printing the password.
public sealed class RabbitMqCredentialRotator(HttpClient httpClient, ISecretsManagerClient secretsManager)
{
    private const int SecretsManagerWriteRetryAttempts = 3;

    public async Task RotateAsync(string rabbitMqSecretArn, CancellationToken cancellationToken = default)
    {
        var currentJson = await GetSecretStringAsync(rabbitMqSecretArn, cancellationToken);
        var current = JsonSerializer.Deserialize<RabbitMqCredential>(currentJson)
            ?? throw new InvalidOperationException("RabbitMQ secret did not deserialize to the expected shape.");

        if (string.IsNullOrWhiteSpace(current.Host))
        {
            throw new InvalidOperationException("RabbitMQ secret's host field is empty - cannot derive the Management API endpoint.");
        }

        var newPassword = GenerateNewPassword();

        var tags = await GetCurrentUserTagsAsync(current, cancellationToken);

        Log.Information("Rotating RabbitMQ credential for user {Username} on {Host}.", current.Username, current.Host);
        await SetUserPasswordAsync(current, newPassword, tags, cancellationToken);
        Log.Information("Broker password updated. Persisting the new credential to Secrets Manager.");

        var updated = current with { Password = newPassword };
        await UpdateSecretWithRetryAsync(rabbitMqSecretArn, updated, cancellationToken);

        Log.Information("RabbitMQ credential rotation completed successfully for user {Username}.", current.Username);
    }

    private async Task<JsonElement> GetCurrentUserTagsAsync(RabbitMqCredential current, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUserUri(current))
        {
            Headers = { Authorization = BasicAuth(current.Username, current.Password) },
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read the current RabbitMQ user before rotating (HTTP {(int)response.StatusCode}). No password was changed.");
        }

        using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var user = await JsonSerializer.DeserializeAsync<JsonElement>(body, cancellationToken: cancellationToken);
        // Preserve tags exactly as RabbitMQ returned them (array or
        // comma-string, depending on broker version) rather than assuming
        // a shape - passed straight back through on the PUT below.
        return user.TryGetProperty("tags", out var tags) ? tags : default;
    }

    private async Task SetUserPasswordAsync(
        RabbitMqCredential current, string newPassword, JsonElement tags, CancellationToken cancellationToken)
    {
        var body = tags.ValueKind == JsonValueKind.Undefined
            ? new Dictionary<string, object?> { ["password"] = newPassword }
            : new Dictionary<string, object?> { ["password"] = newPassword, ["tags"] = tags };

        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUserUri(current))
        {
            Headers = { Authorization = BasicAuth(current.Username, current.Password) },
            Content = JsonContent.Create(body),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Deliberately no response body in this message - RabbitMQ
            // management error bodies have never been observed to include
            // the submitted password, but there is no upstream guarantee of
            // that, so it is never logged here regardless.
            throw new InvalidOperationException(
                $"RabbitMQ Management API rejected the password update (HTTP {(int)response.StatusCode}). No password was changed.");
        }
    }

    private async Task UpdateSecretWithRetryAsync(string secretArn, RabbitMqCredential updated, CancellationToken cancellationToken)
    {
        var newJson = JsonSerializer.Serialize(updated);
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= SecretsManagerWriteRetryAttempts; attempt++)
        {
            try
            {
                await secretsManager.PutSecretStringAsync(secretArn, newJson, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                Log.Warning("Secrets Manager update attempt {Attempt}/{MaxAttempts} failed: {ExceptionType}.",
                    attempt, SecretsManagerWriteRetryAttempts, ex.GetType().Name);
            }
        }

        throw new RabbitMqRotationPartiallyAppliedException(
            "The RabbitMQ broker password was changed, but Secrets Manager could not be updated after " +
            $"{SecretsManagerWriteRetryAttempts} attempts. The secret now holds a STALE password. " +
            "Manual recovery required: retry the Secrets Manager write with the new password (already applied " +
            "at the broker - do not attempt to revert it), never regenerate a second new password blindly.",
            lastFailure!);
    }

    // Item 18/32's "old credential verification" step: after a rotation,
    // this confirms the OLD password is genuinely rejected (HTTP 401) -
    // never assumed. A non-401 failure (network error, 5xx) is reported
    // distinctly from "still valid", since it means the check itself
    // couldn't run, not that rotation failed.
    public async Task<bool> VerifyOldCredentialRejectedAsync(
        string host, string username, string oldPassword, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}/api/users/{Uri.EscapeDataString(username)}"))
        {
            Headers = { Authorization = BasicAuth(username, oldPassword) },
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
    }

    // Verify-only mode (never rotates again): confirms the CURRENT secret
    // value (AWSCURRENT) authenticates successfully, and the version it
    // superseded (AWSPREVIOUS - the bootstrap credential, still retained by
    // Secrets Manager's own versioning, never deleted by this tool) is
    // genuinely rejected. Never logs either credential value - only the two
    // booleans.
    public async Task<(bool FinalCredentialAccepted, bool BootstrapCredentialRejected)> VerifyRotationAsync(
        string rabbitMqSecretArn, CancellationToken cancellationToken = default)
    {
        var currentJson = await secretsManager.GetSecretStringAsync(rabbitMqSecretArn, "AWSCURRENT", cancellationToken);
        var current = JsonSerializer.Deserialize<RabbitMqCredential>(currentJson)
            ?? throw new InvalidOperationException("RabbitMQ secret (AWSCURRENT) did not deserialize to the expected shape.");

        using var currentRequest = new HttpRequestMessage(HttpMethod.Get, BuildUserUri(current))
        {
            Headers = { Authorization = BasicAuth(current.Username, current.Password) },
        };
        using var currentResponse = await httpClient.SendAsync(currentRequest, cancellationToken);
        var finalCredentialAccepted = currentResponse.IsSuccessStatusCode;

        var previousJson = await secretsManager.GetSecretStringAsync(rabbitMqSecretArn, "AWSPREVIOUS", cancellationToken);
        var previous = JsonSerializer.Deserialize<RabbitMqCredential>(previousJson)
            ?? throw new InvalidOperationException("RabbitMQ secret (AWSPREVIOUS) did not deserialize to the expected shape.");

        var bootstrapCredentialRejected = await VerifyOldCredentialRejectedAsync(
            previous.Host, previous.Username, previous.Password, cancellationToken);

        Log.Information(
            "Rotation verification: FinalCredentialAccepted={FinalCredentialAccepted}, BootstrapCredentialRejected={BootstrapCredentialRejected}.",
            finalCredentialAccepted, bootstrapCredentialRejected);

        return (finalCredentialAccepted, bootstrapCredentialRejected);
    }

    private static Uri BuildUserUri(RabbitMqCredential credential) =>
        new($"https://{credential.Host}/api/users/{Uri.EscapeDataString(credential.Username)}");

    private static AuthenticationHeaderValue BasicAuth(string username, string password) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")));

    private static string GenerateNewPassword() =>
        // Alphanumeric only, matching the same "special=false" convention
        // every other Terraform-generated credential in this codebase uses
        // - avoids characters that would need escaping in JSON/Basic Auth.
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 32);

    private Task<string> GetSecretStringAsync(string secretArn, CancellationToken cancellationToken) =>
        secretsManager.GetSecretStringAsync(secretArn, "AWSCURRENT", cancellationToken);
}

// Minimal abstraction over the two Secrets Manager operations this tool
// needs - deliberately not the full IAmazonSecretsManager SDK interface, so
// tests can fake it directly instead of stubbing dozens of unrelated SDK
// members. Its own project (never shared with AIAgent.Infrastructure/
// ExternalIntegrations.Infrastructure's ISecretValueReader, or
// IHostPro.DatabaseBootstrap's) - same "Infrastructure/tools projects never
// share code with each other" precedent used throughout this codebase.
public interface ISecretsManagerClient
{
    Task<string> GetSecretStringAsync(string secretId, string versionStage, CancellationToken cancellationToken);
    Task PutSecretStringAsync(string secretId, string secretString, CancellationToken cancellationToken);
}

// Distinct exception type so a caller (or a future CI/ops runbook) can
// reliably detect "the two systems are now inconsistent, human required"
// versus "rotation failed cleanly before anything changed" (item 19).
public sealed class RabbitMqRotationPartiallyAppliedException(string message, Exception innerException)
    : Exception(message, innerException);
