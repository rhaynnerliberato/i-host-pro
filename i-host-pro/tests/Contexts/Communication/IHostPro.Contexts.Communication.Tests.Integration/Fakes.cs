using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fake versions of the two external readers <see cref="IHostPro.Contexts.Communication.Application.ReservationCreatedCommunicationProcessor"/>
/// depends on (Configuration.Contracts/Reservations.Contracts) — this suite
/// is about Communication's OWN RLS/persistence/state-machine/redelivery/
/// connector behavior against a real PostgreSQL instance, never Configuration's/
/// Reservations' own readers (already covered by their own real-Postgres
/// suites: <c>TemplateReaderTests</c>/<c>ReservationGuestContactReaderTests</c>).
/// The full real-stack round trip (real Template/real guest contact reader)
/// is the separate real transport gate (Fase 9, Checkpoint 1, task #415).
/// </summary>
internal sealed class FakeTemplateReader : ITemplateReader
{
    private readonly ActiveTemplate? _template;

    private FakeTemplateReader(ActiveTemplate? template) => _template = template;

    public static FakeTemplateReader Returning(ActiveTemplate? template) => new(template);

    public Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_template);
}

internal sealed class FakeReservationGuestContactReader : IReservationGuestContactReader
{
    private readonly ReservationGuestContact? _contact;

    private FakeReservationGuestContactReader(ReservationGuestContact? contact) => _contact = contact;

    public static FakeReservationGuestContactReader Returning(ReservationGuestContact? contact) => new(contact);

    public Task<ReservationGuestContact?> GetGuestContactAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_contact);
}

internal sealed class FakeOutboundMessageConnector : IOutboundMessageConnector
{
    private readonly Func<OutboundMessageDispatch, OutboundMessageDispatchResult> _behavior;

    private FakeOutboundMessageConnector(Func<OutboundMessageDispatch, OutboundMessageDispatchResult> behavior) =>
        _behavior = behavior;

    public static FakeOutboundMessageConnector Succeeding() =>
        new(_ => new OutboundMessageDispatchResult(Success: true, FailureReason: null));

    public static FakeOutboundMessageConnector Rejecting(string failureReason) =>
        new(_ => new OutboundMessageDispatchResult(Success: false, FailureReason: failureReason));

    public List<OutboundMessageDispatch> ReceivedDispatches { get; } = [];

    public Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        ReceivedDispatches.Add(dispatch);
        return Task.FromResult(_behavior(dispatch));
    }
}
