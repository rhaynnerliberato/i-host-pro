using System.Text.Json.Serialization;

namespace IHostPro.RabbitMqCredentialRotation;

// Matches exactly the JSON shape modules/amazon-mq/main.tf writes into
// ihostpro/<environment>/rabbitmq (CP5.3B) - host is the same hostname the
// broker's own ConsoleURL uses, so the RabbitMQ Management API base URL is
// derived from it (https://{host}) rather than needing a second,
// independently-configured endpoint to keep in sync.
public sealed record RabbitMqCredential(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("virtualHost")] string VirtualHost,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("useTls")] bool UseTls);
