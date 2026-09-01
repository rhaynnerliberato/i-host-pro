namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// <see cref="Address"/> is this property's OWN address, if it has one —
/// <c>null</c> when it relies on its condominium's instead.
/// <see cref="EffectiveAddress"/> is always resolved; <see cref="EffectiveAddressSource"/>
/// is the stable code <c>"property"</c> or <c>"condominium"</c> (Checkpoint 3
/// plan, item 8/9).
/// </summary>
public sealed record PropertyDetailResponse(
    Guid Id,
    string Code,
    string Name,
    int Capacity,
    Guid? CondominiumId,
    AddressResponse? Address,
    AddressResponse EffectiveAddress,
    string EffectiveAddressSource,
    string? TimeZoneId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
