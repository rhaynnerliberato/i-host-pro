using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbReservationParser;

public class AirbnbReservationDryRunEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvaluateAsync_returns_ParseFailed_when_the_parser_fails()
    {
        var parser = FakeAirbnbReservationReminderParser.Returning(
            AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate));
        var evaluator = new AirbnbReservationDryRunEvaluator(parser, new FakeAirbnbListingTitleMappingRepository());

        var outcome = await evaluator.EvaluateAsync("subject", "body", Now, CancellationToken.None);

        outcome.WouldImport.Should().BeFalse();
        outcome.ParseFailureReason.Should().Be(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate);
    }

    [Fact]
    public async Task EvaluateAsync_returns_PropertyNotResolved_when_no_mapping_exists_for_the_parsed_listing_name()
    {
        var parseResult = AirbnbReservationReminderParseResult.Success(
            "TESTCODE12", "Hóspede Teste", Now.AddDays(2), Now.AddDays(5), 2, "Studio Sem Mapeamento");
        var parser = FakeAirbnbReservationReminderParser.Returning(parseResult);
        var evaluator = new AirbnbReservationDryRunEvaluator(parser, new FakeAirbnbListingTitleMappingRepository());

        var outcome = await evaluator.EvaluateAsync("subject", "body", Now, CancellationToken.None);

        outcome.WouldImport.Should().BeFalse();
        outcome.ExternalReservationIdPresent.Should().BeTrue();
        outcome.DatesParsed.Should().BeTrue();
        outcome.GuestCountParsed.Should().BeTrue();
        outcome.PropertyResolved.Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_returns_Ready_when_the_listing_title_resolves_to_a_property()
    {
        var propertyId = Guid.NewGuid();
        var mapping = AirbnbListingTitleMapping.Create(Guid.NewGuid(), Guid.NewGuid(), "Studio Exemplo Fixture", propertyId, Now);
        var parseResult = AirbnbReservationReminderParseResult.Success(
            "TESTCODE12", "Hóspede Teste", Now.AddDays(2), Now.AddDays(5), 2, "Studio Exemplo Fixture");
        var parser = FakeAirbnbReservationReminderParser.Returning(parseResult);
        var evaluator = new AirbnbReservationDryRunEvaluator(parser, FakeAirbnbListingTitleMappingRepository.WithMapping(mapping));

        var outcome = await evaluator.EvaluateAsync("subject", "body", Now, CancellationToken.None);

        outcome.WouldImport.Should().BeTrue();
        outcome.ResolvedPropertyId.Should().Be(propertyId);
        outcome.ExternalReservationId.Should().Be("TESTCODE12");
    }
}
