using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class ActivatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly Address OwnAddress = Address.Create(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static readonly AddressResult CondominiumAddress = new(
        "59090002", "Rua do Condomínio", "2", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Property CreateDraftWithOwnAddress(Guid? condominiumId = null) =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, condominiumId, OwnAddress, Created);

    private static Property CreateDraftWithCondominium(Guid condominiumId) =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, condominiumId, null, Created);

    private sealed record Fixture(
        FakePropertyRepository Repository,
        FakeCondominiumReader CondominiumReader,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        ActivatePropertyCommandHandler Handler);

    private static Fixture CreateFixture(Property? property, AddressResult? condominiumAddress = null)
    {
        var repository = FakePropertyRepository.WithProperty(property);
        var condominiumReader = FakeCondominiumReader.WithAddress(condominiumAddress);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new ActivatePropertyCommandHandler(repository, condominiumReader, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, condominiumReader, auditWriter, eventCollector, handler);
    }

    private static ActivatePropertyCommand Command(Guid propertyId) => new(TenantId, ActorId, propertyId);

    // ---- Happy path ---------------------------------------------------------

    [Fact]
    public async Task Draft_with_own_address_activates_successfully()
    {
        var property = CreateDraftWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("active");
        property.Status.Should().Be(PropertyStatus.Active);
    }

    [Fact]
    public async Task Inactive_activates_back_to_active()
    {
        var property = CreateDraftWithOwnAddress();
        property.Activate(Created);
        property.Deactivate(Created);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        property.Status.Should().Be(PropertyStatus.Active);
    }

    [Fact]
    public async Task Activating_with_a_condominium_and_no_own_address_resolves_the_effective_address_from_the_condominium()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreateDraftWithCondominium(condominiumId);
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address.Should().BeNull();
        result.Value.EffectiveAddress.Should().Be(CondominiumAddress);
        result.Value.EffectiveAddressSource.Should().Be("condominium");
    }

    // ---- Rejections -----------------------------------------------------------

    [Fact]
    public async Task An_already_active_property_fails_with_PropertyAlreadyActive_and_performs_no_side_effect()
    {
        var property = CreateDraftWithOwnAddress();
        property.Activate(Created);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyActive);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_fails_with_PropertyAlreadyArchived_and_performs_no_side_effect()
    {
        var property = CreateDraftWithOwnAddress();
        property.Archive(Created);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyArchived);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound()
    {
        var fixture = CreateFixture(property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task A_property_with_no_effective_address_source_fails_with_PropertyAddressRequired_and_performs_no_side_effect()
    {
        // Structurally impossible via Property.Create (which already forbids
        // this combination) — this exercises the handler's defensive check,
        // reachable only if a future invariant relaxation ever allows it.
        var property = CreateDraftWithOwnAddress();
        property.ChangeAddress(null, Created);
        property.ChangeCondominium(null, Created);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAddressRequired);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_condominium_that_no_longer_exists_fails_with_CondominiumNotFound_and_performs_no_side_effect()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreateDraftWithCondominium(condominiumId);
        var fixture = CreateFixture(property, condominiumAddress: null);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
        AssertNoSideEffect(fixture);
    }

    // ---- Auditoria / eventos -----------------------------------------------------

    [Fact]
    public async Task Activation_writes_exactly_one_audit_entry_with_the_property_activated_action_code_and_status_only()
    {
        var property = CreateDraftWithOwnAddress();
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Property");
        entry.ActionCode.Should().Be("property_activated");
        entry.AggregateId.Should().Be(property.Id);
        entry.ChangedFields.Should().Equal("status");
    }

    [Fact]
    public async Task Activation_enqueues_exactly_one_PropertyActivated_event_with_correct_ActorId_and_AggregateId()
    {
        var property = CreateDraftWithOwnAddress();
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyActivated>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(property.Id);
        events[0].AggregateType.Should().Be("Property");
        events[0].PropertyId.Should().Be(property.Id);
    }

    [Fact]
    public async Task Activation_bumps_UpdatedAt()
    {
        var property = CreateDraftWithOwnAddress();
        var originalUpdatedAt = property.UpdatedAt;
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        property.UpdatedAt.Should().Be(Now);
        property.UpdatedAt.Should().NotBe(originalUpdatedAt);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var property = CreateDraftWithOwnAddress();
        var fixture = CreateFixture(property);
        using var cts = new CancellationTokenSource();

        var act = async () => await fixture.Handler.Handle(Command(property.Id), cts.Token);

        await act.Should().NotThrowAsync();
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
