using System.Text.Json.Serialization;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Minimal shape of Meta's own <c>statuses</c> webhook envelope (Fase 9,
/// Checkpoint 2.3.2 — traced to developers.facebook.com's status webhook
/// reference, confirmed during Checkpoint 2.3.0's own research). Models
/// ONLY the fields this checkpoint actually reads — never
/// <c>recipient_id</c>/<c>contacts</c>/inbound <c>messages[]</c>/
/// <c>conversation</c>/<c>pricing</c> — those either carry PII this
/// checkpoint must never touch, or aren't needed yet. Confined entirely to
/// <c>Infrastructure.Meta</c> — never referenced outside this namespace
/// (mandate §16).
/// </summary>
public sealed class MetaWebhookEnvelope
{
    [JsonPropertyName("entry")]
    public List<MetaWebhookEntry>? Entry { get; init; }
}

public sealed class MetaWebhookEntry
{
    [JsonPropertyName("changes")]
    public List<MetaWebhookChange>? Changes { get; init; }
}

public sealed class MetaWebhookChange
{
    [JsonPropertyName("value")]
    public MetaWebhookValue? Value { get; init; }
}

public sealed class MetaWebhookValue
{
    [JsonPropertyName("metadata")]
    public MetaWebhookMetadata? Metadata { get; init; }

    /// <summary>Absent entirely for inbound-message webhooks (mandate §17) — never modeled, only checked for presence.</summary>
    [JsonPropertyName("statuses")]
    public List<MetaWebhookStatus>? Statuses { get; init; }
}

public sealed class MetaWebhookMetadata
{
    [JsonPropertyName("phone_number_id")]
    public string? PhoneNumberId { get; init; }
}

public sealed class MetaWebhookStatus
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("errors")]
    public List<MetaWebhookError>? Errors { get; init; }
}

public sealed class MetaWebhookError
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }
}
