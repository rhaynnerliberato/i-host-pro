namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

/// <summary>
/// Parses one Airbnb "reservation reminder" email (subject + HTML or
/// plain-text body) into the fields <c>Reservation.CreateImported</c>
/// requires. Pure/deterministic — no I/O, no database access — so it can be
/// exercised entirely against sanitized fixtures.
/// </summary>
public interface IAirbnbReservationReminderParser
{
    /// <param name="subject">The email's subject line, verbatim.</param>
    /// <param name="body">The email's body — HTML or plain text; implementations normalize either.</param>
    /// <param name="receivedAtUtc">When the email was received — used only to resolve the year for the check-in/check-out dates, which the observed template renders without one.</param>
    AirbnbReservationReminderParseResult Parse(string subject, string body, DateTimeOffset receivedAtUtc);
}
