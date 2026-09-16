using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Never invoked unless a test supplies a real body via the message source fake - existing delta-sync tests pass no body, so this fake's outcome is irrelevant to them.</summary>
internal sealed class FakeAirbnbReservationDryRunEvaluator : IAirbnbReservationDryRunEvaluator
{
    private readonly AirbnbReservationDryRunOutcome _outcome;

    public FakeAirbnbReservationDryRunEvaluator(AirbnbReservationDryRunOutcome? outcome = null) =>
        _outcome = outcome ?? AirbnbReservationDryRunOutcome.ParseFailed(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate);

    public List<(string Subject, string Body)> Calls { get; } = [];

    public Task<AirbnbReservationDryRunOutcome> EvaluateAsync(string subject, string body, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        Calls.Add((subject, body));
        return Task.FromResult(_outcome);
    }
}
