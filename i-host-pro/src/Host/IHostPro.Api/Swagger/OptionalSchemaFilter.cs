using IHostPro.Contexts.PropertyManagement.Application;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace IHostPro.Api.Swagger;

/// <summary>
/// Without this filter, Swashbuckle's default reflection-based schema
/// generation describes PropertyManagement's <c>Optional&lt;T&gt;</c> by its
/// public CLR shape — <c>{ isSet: boolean, value: T }</c> — which does not
/// match what <c>OptionalJsonConverter&lt;T&gt;</c> (PropertyManagement.Api)
/// actually reads/writes on the wire: the bare <c>T</c> value, or JSON
/// <c>null</c>; the JSON property being present at all is what "isSet" means,
/// never a nested wrapper object. Left unfixed, NSwag generates a client that
/// sends the wrong shape and every <c>PATCH</c> carrying an
/// <c>Optional&lt;T&gt;</c> field fails deserialization. This filter replaces
/// the generated schema for any such property with its inner <c>T</c>'s own
/// schema, so the OpenAPI document — and everything generated from it —
/// reflects the converter's real contract. Lives in the Host project (not
/// PropertyManagement.Api) because Swashbuckle is only referenced at the
/// composition root.
/// </summary>
public sealed class OptionalSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (!context.Type.IsGenericType || context.Type.GetGenericTypeDefinition() != typeof(Optional<>))
            return;
        if (schema is not OpenApiSchema mutableSchema)
            return;

        var innerType = context.Type.GetGenericArguments()[0];
        var innerSchema = context.SchemaGenerator.GenerateSchema(innerType, context.SchemaRepository);

        // A complex inner type (e.g. AddressRequest) is registered as its own named
        // component and GenerateSchema returns a $ref to it here, not an inline schema —
        // resolve through the repository to the real definition so there is something to
        // copy Properties/Type from (a bare reference has neither).
        if (innerSchema is not OpenApiSchema mutableInnerSchema)
        {
            var refId = ExtractReferenceId(innerSchema);
            if (refId is null || !context.SchemaRepository.Schemas.TryGetValue(refId, out var resolved) || resolved is not OpenApiSchema resolvedSchema)
                return;
            mutableInnerSchema = resolvedSchema;
        }

        mutableSchema.Type = mutableInnerSchema.Type;
        mutableSchema.Format = mutableInnerSchema.Format;
        mutableSchema.Properties = mutableInnerSchema.Properties;
        mutableSchema.Items = mutableInnerSchema.Items;
        mutableSchema.AllOf = mutableInnerSchema.AllOf;
        mutableSchema.Required = mutableInnerSchema.Required;
        mutableSchema.AdditionalProperties = mutableInnerSchema.AdditionalProperties;
        mutableSchema.Enum = mutableInnerSchema.Enum;
    }

    /// <summary>Extracts the component schema id from a reference-only schema (e.g. <c>OpenApiSchemaReference</c>) via its public <c>Reference</c> property — reflection-based since the concrete reference type isn't referenced directly here.</summary>
    private static string? ExtractReferenceId(IOpenApiSchema schema)
    {
        var referenceProperty = schema.GetType().GetProperty("Reference");
        var reference = referenceProperty?.GetValue(schema);
        if (reference is null)
            return null;
        var idProperty = reference.GetType().GetProperty("Id");
        return idProperty?.GetValue(reference) as string;
    }
}
