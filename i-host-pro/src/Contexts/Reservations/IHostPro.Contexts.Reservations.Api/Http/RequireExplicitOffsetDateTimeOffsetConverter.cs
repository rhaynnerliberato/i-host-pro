using System.Text.Json;
using System.Text.Json.Serialization;

namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// Rejects a JSON <c>DateTimeOffset</c> string that has no explicit UTC/offset
/// designator ('Z', or a '+'/'-' after the 'T' time separator) — real HTTP
/// homologation (Fase 3 §4) proved <c>System.Text.Json</c>'s own built-in
/// <see cref="DateTimeOffset"/> converter silently accepts an offset-less
/// string (defaulting it to UTC) rather than rejecting it, contradicting
/// this API's documented contract ("offset explícito obrigatório"). Once a
/// string is known to carry SOME designator, parsing is delegated back to
/// the reader's own built-in <see cref="Utf8JsonReader.GetDateTimeOffset"/>
/// — this converter only adds the presence check, never reimplements ISO
/// 8601 parsing itself.
///
/// Applied directly (via <c>[property: JsonConverter(...)]</c>) to
/// <c>CreateReservationRequest.CheckInAt</c>/<c>CheckOutAt</c>, and reused by
/// this project's own <c>OptionalJsonConverter{T}</c> for
/// <c>UpdateReservationRequest</c>'s <c>Optional&lt;DateTimeOffset&gt;</c>
/// fields — never registered globally in <c>Program.cs</c>, so no other
/// Bounded Context's <c>DateTimeOffset</c> fields are affected.
/// </summary>
public sealed class RequireExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadRequireExplicitOffset(ref reader);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    /// <summary>
    /// Shared by <see cref="Read"/> and <c>OptionalJsonConverter{T}</c> (for
    /// <c>T == typeof(DateTimeOffset)</c>) so both request shapes enforce the
    /// exact same rule from a single implementation.
    /// </summary>
    public static DateTimeOffset ReadRequireExplicitOffset(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a JSON string for a date/time value.");

        var text = reader.GetString();
        if (text is null || !HasExplicitOffsetDesignator(text))
        {
            throw new JsonException(
                "A date/time value must include an explicit UTC offset (e.g. 'Z' or '+/-HH:mm') — an offset-less value is rejected, never silently assumed to be UTC.");
        }

        return reader.GetDateTimeOffset();
    }

    private static bool HasExplicitOffsetDesignator(string text)
    {
        // The date portion (YYYY-MM-DD) always contains '-' before 'T' —
        // only the time portion (after 'T') may legitimately carry an
        // offset designator ('Z', or '+'/'-' introducing '±HH:mm').
        var tIndex = text.IndexOf('T');
        if (tIndex < 0)
            return false;

        var timePart = text.AsSpan(tIndex + 1);
        return timePart.Contains('Z') || timePart.Contains('z') || timePart.Contains('+') || timePart.Contains('-');
    }
}
