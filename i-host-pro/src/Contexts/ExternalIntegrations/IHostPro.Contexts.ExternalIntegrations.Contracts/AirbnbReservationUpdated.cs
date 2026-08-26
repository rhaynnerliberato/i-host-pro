using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Published by External Integrations when an already-imported Airbnb
/// reservation changes (Fase 9, Checkpoint 3.2). A provider-neutral CURRENT
/// snapshot of the fields Reservations already knows how to change via
/// <c>ChangeProperty</c>/<c>ChangeGuestName</c>/<c>Reschedule</c>/
/// <c>ChangeGuestCount</c> — never a raw provider diff/payload (CP3.2
/// mandate §6). Never carries pricing. The consumer looks the reservation up
/// by <see cref="ExternalReservationId"/> (mirrors
/// <see cref="AirbnbReservationImported"/>'s own idempotency identity) and
/// applies only the mutators whose value actually changed, same convention
/// as <c>UpdateReservationCommandHandler</c>'s own diffing.
/// </summary>
public sealed record AirbnbReservationUpdated : IntegrationEvent
{
    public required string ExternalReservationId { get; init; }

    public required Guid PropertyId { get; init; }

    public required string GuestName { get; init; }

    public required DateTimeOffset CheckInAt { get; init; }

    public required DateTimeOffset CheckOutAt { get; init; }

    public required int GuestCount { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
