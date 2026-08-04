using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Domain;
using Xunit;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Domain;

public class PropertyOwnerLinkTests
{
    [Fact]
    public void Create_sets_all_fields()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var createdByUserId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var link = PropertyOwnerLink.Create(Guid.NewGuid(), tenantId, propertyId, ownerUserId, createdByUserId, now);

        link.TenantId.Should().Be(tenantId);
        link.PropertyId.Should().Be(propertyId);
        link.OwnerUserId.Should().Be(ownerUserId);
        link.CreatedByUserId.Should().Be(createdByUserId);
        link.CreatedAt.Should().Be(now);
    }
}
