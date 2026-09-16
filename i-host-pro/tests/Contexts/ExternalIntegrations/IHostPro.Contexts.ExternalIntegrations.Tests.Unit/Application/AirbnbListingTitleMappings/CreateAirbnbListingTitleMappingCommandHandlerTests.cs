using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbReservationParser;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbListingTitleMappings;

public class CreateAirbnbListingTitleMappingCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_creates_the_mapping_when_the_title_is_not_already_used()
    {
        var repository = new FakeAirbnbListingTitleMappingRepository();
        var handler = new CreateAirbnbListingTitleMappingCommandHandler(repository, TimeProvider.System);
        var propertyId = Guid.NewGuid();

        var result = await handler.Handle(
            new CreateAirbnbListingTitleMappingCommand(TenantId, "Studio Exemplo Fixture", propertyId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ListingTitle.Should().Be("Studio Exemplo Fixture");
        result.Value.PropertyId.Should().Be(propertyId);
        result.Value.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task Handle_rejects_a_duplicate_exact_title_for_the_same_tenant()
    {
        var existing = AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, "Studio Exemplo Fixture", Guid.NewGuid(), Now);
        var repository = FakeAirbnbListingTitleMappingRepository.WithMapping(existing);
        var handler = new CreateAirbnbListingTitleMappingCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new CreateAirbnbListingTitleMappingCommand(TenantId, "Studio Exemplo Fixture", Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle);
    }

    [Fact]
    public async Task Handle_treats_titles_differing_only_by_whitespace_as_the_same_duplicate()
    {
        var existing = AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, "Studio Exemplo Fixture", Guid.NewGuid(), Now);
        var repository = FakeAirbnbListingTitleMappingRepository.WithMapping(existing);
        var handler = new CreateAirbnbListingTitleMappingCommandHandler(repository, TimeProvider.System);

        var result = await handler.Handle(
            new CreateAirbnbListingTitleMappingCommand(TenantId, "  Studio   Exemplo  Fixture  ", Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle);
    }
}
