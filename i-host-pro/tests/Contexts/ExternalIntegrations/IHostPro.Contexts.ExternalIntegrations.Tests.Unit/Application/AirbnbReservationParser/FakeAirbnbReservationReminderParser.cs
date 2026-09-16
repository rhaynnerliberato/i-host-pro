using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbReservationParser;

internal sealed class FakeAirbnbReservationReminderParser : IAirbnbReservationReminderParser
{
    private readonly AirbnbReservationReminderParseResult _result;

    private FakeAirbnbReservationReminderParser(AirbnbReservationReminderParseResult result) => _result = result;

    public static FakeAirbnbReservationReminderParser Returning(AirbnbReservationReminderParseResult result) => new(result);

    public AirbnbReservationReminderParseResult Parse(string subject, string body, DateTimeOffset receivedAtUtc) => _result;
}
