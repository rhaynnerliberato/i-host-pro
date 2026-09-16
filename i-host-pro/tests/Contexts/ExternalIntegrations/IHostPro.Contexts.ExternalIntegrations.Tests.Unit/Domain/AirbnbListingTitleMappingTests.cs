using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbListingTitleMappingTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_all_fields()
    {
        var propertyId = Guid.NewGuid();

        var mapping = AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, "Studio Exemplo Fixture", propertyId, Now);

        mapping.TenantId.Should().Be(TenantId);
        mapping.ListingTitle.Should().Be("Studio Exemplo Fixture");
        mapping.PropertyId.Should().Be(propertyId);
        mapping.CreatedAtUtc.Should().Be(Now);
        mapping.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_trims_and_collapses_internal_whitespace()
    {
        var mapping = AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, "  Studio   Exemplo  Fixture  ", Guid.NewGuid(), Now);

        mapping.ListingTitle.Should().Be("Studio Exemplo Fixture");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_listing_title(string listingTitle)
    {
        var act = () => AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, listingTitle, Guid.NewGuid(), Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangePropertyId_reassigns_the_property()
    {
        var mapping = AirbnbListingTitleMapping.Create(Guid.NewGuid(), TenantId, "Studio Exemplo Fixture", Guid.NewGuid(), Now);
        var newPropertyId = Guid.NewGuid();

        mapping.ChangePropertyId(newPropertyId, Now.AddMinutes(1));

        mapping.PropertyId.Should().Be(newPropertyId);
        mapping.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Normalize_matches_the_normalization_Create_applies()
    {
        AirbnbListingTitleMapping.Normalize("  Studio   Exemplo  Fixture  ").Should().Be("Studio Exemplo Fixture");
    }
}
