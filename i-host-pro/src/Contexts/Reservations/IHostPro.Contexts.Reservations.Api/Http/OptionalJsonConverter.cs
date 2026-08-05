using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.Reservations.Application;

namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// Binds a JSON property to <see cref="Optional{T}"/> — mirrors
/// <c>PropertyManagement.Api.Http.OptionalJsonConverter{T}</c> exactly,
/// except for the <see cref="DateTimeOffset"/> special case below.
/// <see cref="HandleNull"/> is overridden <c>true</c> so <see cref="Read"/>
/// is actually invoked for an explicit JSON <c>null</c> token —
/// System.Text.Json's default behavior short-circuits to <c>default</c> for
/// a null token without calling the converter at all, which would make an
/// explicit-null indistinguishable from an omitted property. An OMITTED
/// JSON property never reaches this converter in the first place.
/// </summary>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    public override bool HandleNull => true;

    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Optional<T>.Of(default);

        // UpdateReservationRequest.CheckInAt/CheckOutAt must enforce the
        // exact same "offset explícito obrigatório" rule as
        // CreateReservationRequest's own fields (RequireExplicitOffsetDateTimeOffsetConverter)
        // — a plain generic JsonSerializer.Deserialize<T> call here would
        // silently fall back to System.Text.Json's own built-in (permissive)
        // DateTimeOffset converter instead.
        if (typeof(T) == typeof(DateTimeOffset))
        {
            var strictValue = RequireExplicitOffsetDateTimeOffsetConverter.ReadRequireExplicitOffset(ref reader);
            return Optional<T>.Of((T)(object)strictValue);
        }

        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return Optional<T>.Of(value);
    }

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.IsSet && value.Value is not null)
            JsonSerializer.Serialize(writer, value.Value, options);
        else
            writer.WriteNullValue();
    }
}
