namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

/// <summary>
/// Ties <see cref="IAirbnbReservationReminderParser"/> and
/// <see cref="AirbnbListingTitleMappings.IAirbnbListingTitleMappingRepository"/>
/// together into one DRY_RUN evaluation — never calls
/// <c>IAirbnbReservationSyncPublisher</c> or any other mutating call.
/// </summary>
public interface IAirbnbReservationDryRunEvaluator
{
    Task<AirbnbReservationDryRunOutcome> EvaluateAsync(
        string subject, string body, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken);
}
