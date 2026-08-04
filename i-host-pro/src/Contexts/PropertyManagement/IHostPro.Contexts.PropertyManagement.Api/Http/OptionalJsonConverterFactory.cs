using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.PropertyManagement.Application;

namespace IHostPro.Contexts.PropertyManagement.Api.Http;

/// <summary>
/// Applied per-property on <c>UpdatePropertyRequest</c>'s record parameters
/// via <c>[property: JsonConverter(typeof(OptionalJsonConverterFactory))]</c>
/// (Checkpoint 3 plan, item 4) — never on <see cref="Optional{T}"/> itself,
/// since that would require Application to reference this Api-layer
/// converter, which Architecture Principles §6 forbids. Creates the closed
/// <see cref="OptionalJsonConverter{T}"/> for whichever <c>T</c> the
/// property actually declares.
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
