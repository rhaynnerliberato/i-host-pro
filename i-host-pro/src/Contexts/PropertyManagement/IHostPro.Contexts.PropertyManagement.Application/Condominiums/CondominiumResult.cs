namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// A condominium's full detail, as seen by an Administrator (Checkpoint 2
/// plan, item 4) — the shape shared by <c>CreateCondominiumCommand</c>'s
/// response, <c>UpdateCondominiumCommand</c>'s response, and
/// <c>GetCondominiumDetailQuery</c>'s result.
/// </summary>
public sealed record CondominiumResult(
    Guid Id,
    string Name,
    AddressResult Address,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
