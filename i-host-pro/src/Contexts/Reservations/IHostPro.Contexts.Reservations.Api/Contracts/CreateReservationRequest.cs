using System.Text.Json.Serialization;
using IHostPro.Contexts.Reservations.Api.Http;

namespace IHostPro.Contexts.Reservations.Api.Contracts;

/// <summary>
/// Every field is nullable/default at the wire level — presence/format is
/// validated by the Application layer (<c>CreateReservationCommandValidator</c>),
/// never here (mirrors <c>CreatePropertyRequest</c>'s convention). No
/// <c>status</c> field: the client can never set it — every reservation is
/// born <c>Confirmed</c>. <see cref="CheckInAt"/>/<see cref="CheckOutAt"/>
/// must carry an explicit UTC offset — enforced by
/// <see cref="RequireExplicitOffsetDateTimeOffsetConverter"/> (real HTTP
/// homologation, Fase 3 §4, proved System.Text.Json's own built-in
/// <c>DateTimeOffset</c> converter does NOT reject an offset-less string on
/// its own — it silently assumes UTC — so this explicit converter is
/// required, not optional).
/// </summary>
public sealed record CreateReservationRequest(
    Guid? PropertyId,
    string? GuestName,
    string? GuestPhone,
    [property: JsonConverter(typeof(RequireExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? CheckInAt,
    [property: JsonConverter(typeof(RequireExplicitOffsetDateTimeOffsetConverter))] DateTimeOffset? CheckOutAt,
    int? GuestCount);
