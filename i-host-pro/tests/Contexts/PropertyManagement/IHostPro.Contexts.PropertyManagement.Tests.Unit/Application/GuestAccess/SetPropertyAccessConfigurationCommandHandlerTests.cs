using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.GuestAccess;

public class SetPropertyAccessConfigurationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly AddressResult SomeAddress = new(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static PropertyResult SomeProperty() => new(
        PropertyId, "STUDIO-1", "Studio 1", 2, null, SomeAddress, SomeAddress, "property", "active", Now, Now);

    [Fact]
    public async Task Handle_fails_when_property_does_not_exist()
    {
        var handler = new SetPropertyAccessConfigurationCommandHandler(
            FakePropertyReader.WithDetail(null), FakePropertyAccessConfigurationRepository.WithExisting(null),
            new FakePropertyAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetPropertyAccessConfigurationCommand(TenantId, ActorId, PropertyId, "front-door-code", "Instructions", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Handle_creates_a_new_configuration_when_none_exists_yet()
    {
        var repository = FakePropertyAccessConfigurationRepository.WithExisting(null);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetPropertyAccessConfigurationCommandHandler(
            FakePropertyReader.WithDetail(SomeProperty()), repository, auditWriter, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetPropertyAccessConfigurationCommand(TenantId, ActorId, PropertyId, "front-door-code", "Instructions", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessCredentialSecretReference.Should().Be("front-door-code");
        result.Value.AccessInstructions.Should().Be("Instructions");
        result.Value.IsActive.Should().BeTrue();
        repository.AddedConfigurations.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "property_access_configuration_created");
    }

    [Fact]
    public async Task Handle_updates_an_existing_configuration_when_a_field_changes()
    {
        var existing = PropertyAccessConfiguration.Create(Guid.NewGuid(), TenantId, PropertyId, "old-reference", "Old instructions", true, Now);
        var repository = FakePropertyAccessConfigurationRepository.WithExisting(existing);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetPropertyAccessConfigurationCommandHandler(
            FakePropertyReader.WithDetail(SomeProperty()), repository, auditWriter, new FixedTimeProvider(Now.AddDays(1)));

        var result = await handler.Handle(
            new SetPropertyAccessConfigurationCommand(TenantId, ActorId, PropertyId, "new-reference", "Old instructions", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessCredentialSecretReference.Should().Be("new-reference");
        repository.AddedConfigurations.Should().BeEmpty("this is an update, never a second row for the same Property");
        repository.UpdatedConfigurations.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "property_access_configuration_updated");
    }

    [Fact]
    public async Task Handle_is_idempotent_when_every_field_already_matches()
    {
        var existing = PropertyAccessConfiguration.Create(Guid.NewGuid(), TenantId, PropertyId, "front-door-code", "Instructions", true, Now);
        var repository = FakePropertyAccessConfigurationRepository.WithExisting(existing);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetPropertyAccessConfigurationCommandHandler(
            FakePropertyReader.WithDetail(SomeProperty()), repository, auditWriter, new FixedTimeProvider(Now.AddDays(1)));

        var result = await handler.Handle(
            new SetPropertyAccessConfigurationCommand(TenantId, ActorId, PropertyId, "front-door-code", "Instructions", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repository.UpdatedConfigurations.Should().BeEmpty("no field changed — a no-op request must mutate nothing");
        auditWriter.RecordedEntries.Should().BeEmpty("no audit entry for a no-op request");
    }

    [Fact]
    public async Task Handle_never_persists_a_raw_credential_only_a_reference()
    {
        // A sentinel value that LOOKS LIKE a raw password/PIN — the handler
        // must treat it exactly like any other reference string (it never
        // resolves or validates it) — proving this command truly never
        // touches a secret store, only a reference (CP6.2 mandate item 4).
        const string sentinelReference = "NOT-A-REAL-SECRET-JUST-A-REFERENCE-NAME";
        var repository = FakePropertyAccessConfigurationRepository.WithExisting(null);
        var handler = new SetPropertyAccessConfigurationCommandHandler(
            FakePropertyReader.WithDetail(SomeProperty()), repository, new FakePropertyAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetPropertyAccessConfigurationCommand(TenantId, ActorId, PropertyId, sentinelReference, null, true),
            CancellationToken.None);

        result.Value.AccessCredentialSecretReference.Should().Be(sentinelReference,
            "the command persists the reference verbatim — it never resolves or transforms it");
    }
}
