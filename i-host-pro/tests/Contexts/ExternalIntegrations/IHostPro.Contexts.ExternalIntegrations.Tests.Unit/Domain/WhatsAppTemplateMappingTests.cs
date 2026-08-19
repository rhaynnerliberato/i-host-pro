using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class WhatsAppTemplateMappingTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_stores_the_provided_fields()
    {
        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR",
            ["CheckInDate"], Now);

        mapping.TenantId.Should().Be(TenantId);
        mapping.TemplateKey.Should().Be("RESERVATION_CONFIRMATION");
        mapping.ProviderTemplateName.Should().Be("reservation_confirmation_v1");
        mapping.LanguageCode.Should().Be("pt_BR");
        mapping.ParameterOrder.Should().Equal("CheckInDate");
        mapping.CreatedAtUtc.Should().Be(Now);
        mapping.UpdatedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_templateKey(string templateKey)
    {
        var act = () => WhatsAppTemplateMapping.Create(Guid.NewGuid(), TenantId, templateKey, "name", "pt_BR", [], Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_providerTemplateName(string providerTemplateName)
    {
        var act = () => WhatsAppTemplateMapping.Create(Guid.NewGuid(), TenantId, "KEY", providerTemplateName, "pt_BR", [], Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_languageCode(string languageCode)
    {
        var act = () => WhatsAppTemplateMapping.Create(Guid.NewGuid(), TenantId, "KEY", "name", languageCode, [], Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateMapping_replaces_the_provider_fields_and_stamps_UpdatedAtUtc()
    {
        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "old_name", "en_US", ["A"], Now);
        var updatedAt = Now.AddMinutes(5);

        mapping.UpdateMapping("new_name", "pt_BR", ["CheckInDate", "GuestName"], updatedAt);

        mapping.ProviderTemplateName.Should().Be("new_name");
        mapping.LanguageCode.Should().Be("pt_BR");
        mapping.ParameterOrder.Should().Equal("CheckInDate", "GuestName");
        mapping.UpdatedAtUtc.Should().Be(updatedAt);
        mapping.TemplateKey.Should().Be("RESERVATION_CONFIRMATION", "the key is the upsert identity — never changed by an update");
    }
}
