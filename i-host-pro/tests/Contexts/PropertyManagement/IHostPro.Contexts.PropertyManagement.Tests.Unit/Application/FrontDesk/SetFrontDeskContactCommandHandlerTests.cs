using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.FrontDesk;

public class SetFrontDeskContactCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid CondominiumId = Guid.NewGuid();

    private static CondominiumResult SomeCondominium() => new(
        CondominiumId, "Condominio Teste",
        new AddressResult("01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP", "BR"),
        Now, Now);

    [Fact]
    public async Task Handle_fails_when_condominium_does_not_exist()
    {
        var handler = new SetFrontDeskContactCommandHandler(
            FakeCondominiumReader.WithDetail(null), FakeFrontDeskContactRepository.WithExisting(null),
            new FakePropertyAuditWriter(), new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetFrontDeskContactCommand(TenantId, ActorId, CondominiumId, "Portaria Bloco A", "+5511977776666", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
    }

    [Fact]
    public async Task Handle_creates_a_new_contact_when_none_exists_yet()
    {
        var repository = FakeFrontDeskContactRepository.WithExisting(null);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetFrontDeskContactCommandHandler(
            FakeCondominiumReader.WithDetail(SomeCondominium()), repository, auditWriter, new FixedTimeProvider(Now));

        var result = await handler.Handle(
            new SetFrontDeskContactCommand(TenantId, ActorId, CondominiumId, "Portaria Bloco A", "+5511977776666", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DisplayName.Should().Be("Portaria Bloco A");
        result.Value.PhoneNumber.Should().Be("+5511977776666");
        result.Value.IsActive.Should().BeTrue();
        repository.AddedContacts.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "front_desk_contact_created");
    }

    [Fact]
    public async Task Handle_updates_an_existing_contact_when_a_field_changes()
    {
        var existing = FrontDeskContact.Create(Guid.NewGuid(), TenantId, CondominiumId, "Portaria Bloco A", "+5511977776666", true, Now);
        var repository = FakeFrontDeskContactRepository.WithExisting(existing);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetFrontDeskContactCommandHandler(
            FakeCondominiumReader.WithDetail(SomeCondominium()), repository, auditWriter, new FixedTimeProvider(Now.AddDays(1)));

        var result = await handler.Handle(
            new SetFrontDeskContactCommand(TenantId, ActorId, CondominiumId, "Portaria Bloco A", "+5511988885555", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PhoneNumber.Should().Be("+5511988885555");
        repository.AddedContacts.Should().BeEmpty("this is an update, never a second row for the same Condominium");
        repository.UpdatedContacts.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.ActionCode == "front_desk_contact_updated");
    }

    [Fact]
    public async Task Handle_is_idempotent_when_every_field_already_matches()
    {
        var existing = FrontDeskContact.Create(Guid.NewGuid(), TenantId, CondominiumId, "Portaria Bloco A", "+5511977776666", true, Now);
        var repository = FakeFrontDeskContactRepository.WithExisting(existing);
        var auditWriter = new FakePropertyAuditWriter();
        var handler = new SetFrontDeskContactCommandHandler(
            FakeCondominiumReader.WithDetail(SomeCondominium()), repository, auditWriter, new FixedTimeProvider(Now.AddDays(1)));

        var result = await handler.Handle(
            new SetFrontDeskContactCommand(TenantId, ActorId, CondominiumId, "Portaria Bloco A", "+5511977776666", true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repository.UpdatedContacts.Should().BeEmpty("no field changed — a no-op request must mutate nothing");
        auditWriter.RecordedEntries.Should().BeEmpty("no audit entry for a no-op request");
    }

    [Fact]
    public async Task Handle_publishes_no_integration_event()
    {
        // Fase 10, Checkpoint 4 mandate §34: resolution is synchronous
        // (IFrontDeskContactReader) — no FrontDeskContactCreated/Updated
        // event exists to be published. This test documents that
        // SetFrontDeskContactCommandHandler's constructor takes no
        // IIntegrationEventCollector at all (a compile-time guarantee,
        // exercised here for visibility).
        var handlerConstructorParameters = typeof(SetFrontDeskContactCommandHandler)
            .GetConstructors()[0].GetParameters();

        handlerConstructorParameters.Should().NotContain(
            p => p.ParameterType.Name.Contains("IntegrationEventCollector"));
    }
}
