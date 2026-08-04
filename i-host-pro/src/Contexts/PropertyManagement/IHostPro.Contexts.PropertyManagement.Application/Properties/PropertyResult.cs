using IHostPro.Contexts.PropertyManagement.Application.Condominiums;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// A property's full administrative detail (Checkpoint 3 plan, item 8) — the
/// shape shared by <c>CreatePropertyCommand</c>'s response,
/// <c>UpdatePropertyCommand</c>'s response, and <c>GetPropertyDetailQuery</c>'s
/// result. <see cref="Address"/> is this property's OWN address, if it has
/// one (<c>null</c> when it relies on its condominium's address instead).
/// <see cref="EffectiveAddress"/> is always resolved (Checkpoint 3 plan, item
/// 9): the property's own address when it has one, otherwise its
/// condominium's — never auto-copied into <see cref="Address"/> itself.
/// <see cref="EffectiveAddressSource"/> is the stable code
/// <c>"property"</c> or <c>"condominium"</c>, never a localized/free-form
/// value.
/// </summary>
public sealed record PropertyResult(
    Guid Id,
    string Code,
    string Name,
    int Capacity,
    Guid? CondominiumId,
    AddressResult? Address,
    AddressResult EffectiveAddress,
    string EffectiveAddressSource,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
