using System.Text.Json.Serialization;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta Cloud API's own <c>POST /{phone-number-id}/messages</c> request
/// shape for a template message (Fase 9, Checkpoint 2.2 — traced to
/// developers.facebook.com/docs/whatsapp/cloud-api/reference/messages/ and
/// .../guides/send-messages/, fetched during this checkpoint's own research).
/// Meta-specific — lives ONLY in <c>ExternalIntegrations.Infrastructure</c>,
/// never in <c>Contracts</c> (ADR-021).
/// </summary>
public sealed class MetaSendTemplateMessageRequest
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; init; } = "whatsapp";

    [JsonPropertyName("recipient_type")]
    public string RecipientType { get; init; } = "individual";

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "template";

    [JsonPropertyName("template")]
    public required MetaTemplate Template { get; init; }
}

public sealed class MetaTemplate
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("language")]
    public required MetaTemplateLanguage Language { get; init; }

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MetaTemplateComponent>? Components { get; init; }
}

public sealed class MetaTemplateLanguage
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }
}

/// <summary>
/// Only the "body" component with positional text parameters is modeled —
/// the one shape CP1's single real template (<c>RESERVATION_CONFIRMATION</c>)
/// actually needs (mandate §17: minimal fields only, no generic template
/// engine). Header/button components are not modeled — never added
/// speculatively ahead of a real template that needs them.
/// </summary>
public sealed class MetaTemplateComponent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("parameters")]
    public required IReadOnlyList<MetaTemplateParameter> Parameters { get; init; }
}

public sealed class MetaTemplateParameter
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
