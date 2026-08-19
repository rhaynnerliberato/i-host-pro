using System.Text.Json.Serialization;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta Cloud API's success response shape for <c>POST /messages</c> — traced
/// to the official reference (Fase 9, Checkpoint 2.2 research). The wamid
/// string format itself is not documented by Meta as a fixed grammar — this
/// type treats <see cref="MetaMessageId.Id"/> as an opaque string, never
/// pattern-validated.
/// </summary>
public sealed class MetaSendMessageResponse
{
    [JsonPropertyName("messaging_product")]
    public string? MessagingProduct { get; init; }

    [JsonPropertyName("contacts")]
    public IReadOnlyList<MetaContact>? Contacts { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<MetaMessageId>? Messages { get; init; }
}

public sealed class MetaContact
{
    [JsonPropertyName("input")]
    public string? Input { get; init; }

    [JsonPropertyName("wa_id")]
    public string? WaId { get; init; }
}

public sealed class MetaMessageId
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
