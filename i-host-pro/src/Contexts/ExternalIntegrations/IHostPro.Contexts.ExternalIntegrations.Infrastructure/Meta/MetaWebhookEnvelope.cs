using System.Text.Json.Serialization;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Shape of Meta's own <c>statuses</c> AND <c>messages</c> webhook envelopes
/// (Fase 9, Checkpoint 2.3.2 for <c>statuses</c>; Fase 11, Checkpoint 1 for
/// <c>messages</c> — traced to developers.facebook.com's own webhook
/// references). Models ONLY the fields actually read — never
/// <c>recipient_id</c>/<c>contacts</c>/<c>conversation</c>/<c>pricing</c>,
/// and for <c>messages[]</c>, never any type other than <c>text</c> (image/
/// audio/video/document/location/... are classified
/// <see cref="InboundGuestMessageType.Unsupported"/> downstream without
/// their type-specific payload ever being modeled here). Confined entirely
/// to <c>Infrastructure.Meta</c> — never referenced outside this namespace
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

    /// <summary>Absent entirely for inbound-message webhooks — never modeled together with <see cref="Messages"/> in the same change.</summary>
    [JsonPropertyName("statuses")]
    public List<MetaWebhookStatus>? Statuses { get; init; }

    /// <summary>Absent entirely for status webhooks (Fase 11, Checkpoint 1) — never modeled together with <see cref="Statuses"/> in the same change.</summary>
    [JsonPropertyName("messages")]
    public List<MetaWebhookMessage>? Messages { get; init; }
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

/// <summary>One inbound guest message entry (Fase 11, Checkpoint 1). <see cref="Text"/> is populated only when <see cref="Type"/> is <c>"text"</c> — every other type's own payload (image/audio/video/document/location/...) is deliberately never modeled (TEXT ONLY, mandate item 24).</summary>
public sealed class MetaWebhookMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Meta's own <c>wa_id</c>-shaped sender phone (digits only, no leading <c>+</c>, per WhatsApp Cloud API convention) — reduced further via a digits-only filter before crossing into the Integration Event, never trusted as already-canonical.</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("text")]
    public MetaWebhookMessageText? Text { get; init; }
}

public sealed class MetaWebhookMessageText
{
    [JsonPropertyName("body")]
    public string? Body { get; init; }
}
