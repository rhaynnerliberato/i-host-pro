using System.Text.Json.Serialization;
using IHostPro.Contexts.PropertyManagement.Api.Http;
using IHostPro.Contexts.PropertyManagement.Application;

namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// Every field is independently <see cref="Optional{T}"/>, bound via
/// <see cref="OptionalJsonConverterFactory"/> (Checkpoint 3 plan, item 4) —
/// omitted means "leave unchanged"; at least one must be supplied. No
/// <c>status</c>/<c>tenantId</c>/<c>actorId</c>/<c>ownerUserId</c> field: the
/// client can never set those (Checkpoint 3 plan, item 2).
///
/// <see cref="CondominiumId"/>: omitted keeps the current link, explicit
/// <c>null</c> removes it, a supplied id reassigns it. <see cref="Address"/>:
/// omitted keeps the current own address, explicit <c>null</c> removes it, a
/// supplied object replaces it wholesale — never a partial patch.
/// </summary>
public sealed record UpdatePropertyRequest(
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<string> Code,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<string> Name,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<int> Capacity,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<Guid?> CondominiumId,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<AddressRequest?> Address);
