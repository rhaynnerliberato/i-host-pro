using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.Reservations.Application;

namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// Applied per-property on <c>UpdateReservationRequest</c>'s record
/// parameters via <c>[property: JsonConverter(typeof(OptionalJsonConverterFactory))]</c>
/// — mirrors <c>PropertyManagement.Api.Http.OptionalJsonConverterFactory</c>
/// exactly. Never applied to <see cref="Optional{T}"/> itself, since that
/// would require Application to reference this Api-layer converter, which
/// Architecture Principles §6 forbids.
/// </summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var innerType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(innerType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}
