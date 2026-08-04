namespace IHostPro.Contexts.PropertyManagement.Application;

/// <summary>
/// A presence-aware value: distinguishes "not supplied at all" (<see cref="IsSet"/>
/// <c>false</c>) from "supplied, possibly as <c>null</c>" (<see cref="IsSet"/>
/// <c>true</c>, <see cref="Value"/> possibly <c>null</c>) — Checkpoint 3
/// plan, item 4: "É obrigatório distinguir: campo omitido; campo
/// explicitamente informado como null." Used by <c>UpdatePropertyCommand</c>'s
/// fields, since a plain nullable cannot express this third state
/// (Checkpoint 3 plan, item 4: "não usar... nullable simples que confunda
/// campo omitido com null").
///
/// Deliberately local to Property Management's own Application layer, not
/// <c>BuildingBlocks</c> (Checkpoint 3 plan, item 4: "flag genérica em
/// BuildingBlocks sem necessidade comprovada") — no other Bounded Context has
/// a proven need for this yet. Carries no JSON/ASP.NET Core dependency of
/// its own: the actual JSON binding (<c>OptionalJsonConverter{T}</c>) lives
/// in <c>PropertyManagement.Api</c>, applied per-property via
/// <c>[JsonConverter(typeof(OptionalJsonConverterFactory))]</c> on the
/// request DTO — this type is referenced by both layers without either
/// depending on the other's framework (Api depends on Application, never the
/// reverse).
/// </summary>
public readonly record struct Optional<T>
{
    public bool IsSet { get; }

    public T? Value { get; }

    private Optional(bool isSet, T? value)
    {
        IsSet = isSet;
        Value = value;
    }

    public static Optional<T> Unset { get; } = new(false, default);

    public static Optional<T> Of(T? value) => new(true, value);
}
