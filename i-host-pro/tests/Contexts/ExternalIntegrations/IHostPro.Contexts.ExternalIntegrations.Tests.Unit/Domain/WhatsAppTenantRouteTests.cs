using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class WhatsAppTenantRouteTests
{
    [Fact]
    public void Create_sets_the_phone_number_id_and_tenant_id()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var route = WhatsAppTenantRoute.Create(id, "phone-1", tenantId, now);

        route.Id.Should().Be(id);
        route.PhoneNumberId.Should().Be("phone-1");
        route.TenantId.Should().Be(tenantId);
        route.CreatedAtUtc.Should().Be(now);
        route.UpdatedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_phone_number_id(string? phoneNumberId)
    {
        var act = () => WhatsAppTenantRoute.Create(Guid.NewGuid(), phoneNumberId!, Guid.NewGuid(), DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdatePhoneNumberId_replaces_the_value_and_stamps_updatedAt()
    {
        var route = WhatsAppTenantRoute.Create(Guid.NewGuid(), "old-phone", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(5);

        route.UpdatePhoneNumberId("new-phone", updatedAt);

        route.PhoneNumberId.Should().Be("new-phone");
        route.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UpdatePhoneNumberId_rejects_an_empty_value(string? phoneNumberId)
    {
        var route = WhatsAppTenantRoute.Create(Guid.NewGuid(), "old-phone", Guid.NewGuid(), DateTimeOffset.UtcNow);

        var act = () => route.UpdatePhoneNumberId(phoneNumberId!, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
