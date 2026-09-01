using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Updates a property's code, name, capacity, condominium link and/or own
/// address, idempotently (Checkpoint 3 plan, item 4/5). <see cref="TenantId"/>/
/// <see cref="ActorId"/> come exclusively from the authenticated
/// Administrator's access token claims. Every field is <see cref="Optional{T}"/>
/// to distinguish "omitted" (<c>IsSet == false</c>, leave unchanged) from
/// "explicitly supplied" — at least one must be set (Checkpoint 3 plan, item
/// 5), checked by the handler, mirroring <c>UpdateCondominiumCommand</c>'s
/// <c>NoChangesProvided</c> precedent.
///
/// <see cref="CondominiumId"/>: omitted keeps the current link; an explicit
/// <c>null</c> value removes it; a supplied id reassigns it (Checkpoint 3
/// plan, item 4). <see cref="Address"/>: omitted keeps the current own
/// address; an explicit <c>null</c> value removes it; a supplied object
/// replaces it wholesale, never a partial/field-level patch (Checkpoint 3
/// plan, item 4).
///
/// <see cref="TimeZoneId"/> (Fase 11, Checkpoint 7): omitted keeps the
/// current value; an explicit <c>null</c> removes it; a supplied value must
/// be a genuine IANA time zone id (validated by the handler) and replaces it
/// wholesale.
/// </summary>
public sealed record UpdatePropertyCommand(
    Guid TenantId,
    Guid ActorId,
    Guid PropertyId,
    Optional<string> Code,
    Optional<string> Name,
    Optional<int> Capacity,
    Optional<Guid?> CondominiumId,
    Optional<PropertyAddressInput?> Address,
    Optional<string?> TimeZoneId = default) : ICommand<PropertyResult>;
