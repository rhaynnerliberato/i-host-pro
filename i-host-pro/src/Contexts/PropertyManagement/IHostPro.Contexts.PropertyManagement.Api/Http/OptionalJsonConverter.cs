using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.PropertyManagement.Application;

namespace IHostPro.Contexts.PropertyManagement.Api.Http;

/// <summary>
/// Binds a JSON property to <see cref="Optional{T}"/> (Checkpoint 3 plan,
/// item 4). <see cref="HandleNull"/> is overridden <c>true</c> so <see cref="Read"/>
/// is actually invoked for an explicit JSON <c>null</c> token — System.Text.Json's
/// default behavior short-circuits to <c>default</c> for a null token without
/// calling the converter at all, which would make an explicit-null
/// indistinguishable from an omitted property. An OMITTED JSON property never
/// reaches this converter in the first place: System.Text.Json only invokes a
/// property's converter for properties actually present in the payload — a
/// missing one leaves the corresponding record constructor parameter at
/// <c>default(Optional{T})</c>, which is exactly <see cref="Optional{T}.Unset"/>
/// (a plain struct's all-zero default: <c>IsSet == false</c>).
/// </summary>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    public override bool HandleNull => true;

    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Optional<T>.Of(default);

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
