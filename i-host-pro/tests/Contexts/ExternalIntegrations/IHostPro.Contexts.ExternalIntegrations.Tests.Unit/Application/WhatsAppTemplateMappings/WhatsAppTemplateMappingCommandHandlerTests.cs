using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppTemplateMappings;

public class WhatsAppTemplateMappingCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider FixedTime = new(Now);

    [Fact]
    public async Task ConfigureWhatsAppTemplateMappingCommandHandler_creates_a_new_mapping()
    {
        var repository = FakeWhatsAppTemplateMappingRepository.WithExisting(null);
        var handler = new ConfigureWhatsAppTemplateMappingCommandHandler(repository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppTemplateMappingCommand(
                TenantId, ActorUserId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR", ["CheckInDate"]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.TemplateKey.Should().Be("RESERVATION_CONFIRMATION");
        result.Value.ProviderTemplateName.Should().Be("reservation_confirmation_v1");
        result.Value.LanguageCode.Should().Be("pt_BR");
        result.Value.ParameterOrder.Should().Equal("CheckInDate");
        result.Value.CreatedAtUtc.Should().Be(Now);
        repository.AddedMappings.Should().ContainSingle();
    }

    [Fact]
    public async Task ConfigureWhatsAppTemplateMappingCommandHandler_upserts_the_existing_mapping_for_the_same_templateKey()
    {
        var existing = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "old_name", "en_US", ["A"], Now);
        var repository = FakeWhatsAppTemplateMappingRepository.WithExisting(existing);
        var handler = new ConfigureWhatsAppTemplateMappingCommandHandler(repository, FixedTime);

        var result = await handler.Handle(
            new ConfigureWhatsAppTemplateMappingCommand(
                TenantId, ActorUserId, "RESERVATION_CONFIRMATION", "new_name", "pt_BR", ["CheckInDate"]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProviderTemplateName.Should().Be("new_name");
        result.Value.LanguageCode.Should().Be("pt_BR");
        repository.AddedMappings.Should().BeEmpty("an existing mapping for the same TemplateKey must be updated in place, never re-added");
    }

    [Fact]
    public async Task GetWhatsAppTemplateMappingQueryHandler_returns_not_configured_when_none_exists_yet()
    {
        var repository = FakeWhatsAppTemplateMappingRepository.WithExisting(null);
        var handler = new GetWhatsAppTemplateMappingQueryHandler(repository);

        var result = await handler.Handle(new GetWhatsAppTemplateMappingQuery(TenantId, "RESERVATION_CONFIRMATION"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a tenant with no mapping configured yet is a legitimate state, never an error");
        result.Value.TemplateKey.Should().Be("RESERVATION_CONFIRMATION");
        result.Value.ProviderTemplateName.Should().BeNull();
        result.Value.ParameterOrder.Should().BeEmpty();
        result.Value.CreatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetWhatsAppTemplateMappingQueryHandler_returns_the_existing_mapping()
    {
        var existing = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), TenantId, "RESERVATION_CONFIRMATION", "reservation_confirmation_v1", "pt_BR", ["CheckInDate"], Now);
        var repository = FakeWhatsAppTemplateMappingRepository.WithExisting(existing);
        var handler = new GetWhatsAppTemplateMappingQueryHandler(repository);

        var result = await handler.Handle(new GetWhatsAppTemplateMappingQuery(TenantId, "RESERVATION_CONFIRMATION"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProviderTemplateName.Should().Be("reservation_confirmation_v1");
        result.Value.ParameterOrder.Should().Equal("CheckInDate");
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
