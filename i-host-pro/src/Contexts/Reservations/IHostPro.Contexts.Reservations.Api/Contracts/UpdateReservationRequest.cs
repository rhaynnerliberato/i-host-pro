using System.Text.Json.Serialization;
using IHostPro.Contexts.Reservations.Api.Http;
using IHostPro.Contexts.Reservations.Application;

namespace IHostPro.Contexts.Reservations.Api.Contracts;

/// <summary>
/// Every field is independently <see cref="Optional{T}"/>, bound via
/// <see cref="OptionalJsonConverterFactory"/> — omitted means "leave
/// unchanged"; at least one must be supplied. No <c>status</c>/<c>tenantId</c>/
/// <c>actorId</c> field: the client can never set those.
///
/// Only <see cref="GuestPhone"/> accepts an explicit <c>null</c> to remove
/// it — every other field, when supplied, must carry a genuine value (Fase
/// 3, Incremento 1 plan, item 8: "demais campos não aceitam null"); the
/// Application-layer validator rejects the type-default value an explicit
/// JSON <c>null</c> decodes to for those. <see cref="CheckInAt"/>/
/// <see cref="CheckOutAt"/> must carry an explicit UTC offset — enforced by
/// <c>OptionalJsonConverter{T}</c>'s own <see cref="DateTimeOffset"/> special
/// case (<see cref="RequireExplicitOffsetDateTimeOffsetConverter"/>), same
/// rule as <c>CreateReservationRequest</c>'s fields (Fase 3 §4).
/// </summary>
public sealed record UpdateReservationRequest(
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<Guid> PropertyId,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<string> GuestName,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<string?> GuestPhone,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<DateTimeOffset> CheckInAt,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<DateTimeOffset> CheckOutAt,
    [property: JsonConverter(typeof(OptionalJsonConverterFactory))] Optional<int> GuestCount);
