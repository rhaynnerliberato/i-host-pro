using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbListingMappingTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid IntegrationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_all_fields()
    {
        var propertyId = Guid.NewGuid();

        var mapping = AirbnbListingMapping.Create(Guid.NewGuid(), TenantId, IntegrationId, "listing-123", propertyId, Now);

        mapping.TenantId.Should().Be(TenantId);
        mapping.AirbnbIntegrationId.Should().Be(IntegrationId);
        mapping.ExternalListingId.Should().Be("listing-123");
        mapping.PropertyId.Should().Be(propertyId);
        mapping.CreatedAtUtc.Should().Be(Now);
        mapping.UpdatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_trims_the_external_listing_id()
    {
        var mapping = AirbnbListingMapping.Create(Guid.NewGuid(), TenantId, IntegrationId, "  listing-456  ", Guid.NewGuid(), Now);

        mapping.ExternalListingId.Should().Be("listing-456");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_external_listing_id(string externalListingId)
    {
        var act = () => AirbnbListingMapping.Create(Guid.NewGuid(), TenantId, IntegrationId, externalListingId, Guid.NewGuid(), Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangePropertyId_reassigns_the_property()
    {
        var mapping = AirbnbListingMapping.Create(Guid.NewGuid(), TenantId, IntegrationId, "listing-789", Guid.NewGuid(), Now);
        var newPropertyId = Guid.NewGuid();

        mapping.ChangePropertyId(newPropertyId, Now.AddMinutes(1));

        mapping.PropertyId.Should().Be(newPropertyId);
        mapping.UpdatedAtUtc.Should().Be(Now.AddMinutes(1));
    }
}
