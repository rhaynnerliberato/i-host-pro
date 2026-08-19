using System.Text.Json.Serialization;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta Graph API's standard error envelope — traced to the official Cloud
/// API error-codes reference (Fase 9, Checkpoint 2.2 research). Meta's own
/// documented recommendation is to branch on <see cref="MetaError.Code"/>/
/// <see cref="MetaErrorData.Details"/> rather than HTTP status — this
/// provider does both (see <c>MetaWhatsAppMessagingProvider</c>'s own error
/// mapping), since Meta does not guarantee a stable HTTP-status-to-code
/// table.
/// </summary>
public sealed class MetaErrorResponse
{
    [JsonPropertyName("error")]
    public MetaError? Error { get; init; }
}

public sealed class MetaError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("error_subcode")]
    public int? ErrorSubcode { get; init; }

    [JsonPropertyName("fbtrace_id")]
    public string? FbTraceId { get; init; }

    [JsonPropertyName("error_data")]
    public MetaErrorData? ErrorData { get; init; }
}

public sealed class MetaErrorData
{
    [JsonPropertyName("details")]
    public string? Details { get; init; }
}
