using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class DeactivatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly Address OwnAddress = Address.Create(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Property CreateDraftWithOwnAddress() =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, null, OwnAddress, Created);

    private static Property CreateActive()
    {
        var property = CreateDraftWithOwnAddress();
        property.Activate(Created);
        return property;
    }

    private sealed record Fixture(
        FakePropertyRepository Repository,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        DeactivatePropertyCommandHandler Handler);

    private static Fixture CreateFixture(Property? property)
    {
        var repository = FakePropertyRepository.WithProperty(property);
        var condominiumReader = FakeCondominiumReader.WithAddress(null);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new DeactivatePropertyCommandHandler(repository, condominiumReader, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, handler);
    }

    private static DeactivatePropertyCommand Command(Guid propertyId) => new(TenantId, ActorId, propertyId);

    // ---- Happy path ---------------------------------------------------------

    [Fact]
    public async Task An_active_property_deactivates_successfully()
    {
        var property = CreateActive();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("inactive");
        property.Status.Should().Be(PropertyStatus.Inactive);
    }

    // ---- Rejections -----------------------------------------------------------

    [Fact]
    public async Task An_already_inactive_property_fails_with_PropertyAlreadyInactive_and_performs_no_side_effect()
    {
        var property = CreateActive();
        property.Deactivate(Created);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyInactive);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_draft_property_fails_with_InvalidPropertyStatusTransition_and_performs_no_side_effect()
    {
        var property = CreateDraftWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.InvalidPropertyStatusTransition);
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

    // ---- Auditoria / eventos -----------------------------------------------------

    [Fact]
    public async Task Deactivation_writes_exactly_one_audit_entry_with_the_property_deactivated_action_code_and_status_only()
    {
        var property = CreateActive();
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.ActionCode.Should().Be("property_deactivated");
        entry.ChangedFields.Should().Equal("status");
    }

    [Fact]
    public async Task Deactivation_enqueues_exactly_one_PropertyDeactivated_event()
    {
        var property = CreateActive();
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyDeactivated>().ToArray();
        events.Should().ContainSingle();
        events[0].PropertyId.Should().Be(property.Id);
    }

    [Fact]
    public async Task Deactivation_bumps_UpdatedAt()
    {
        var property = CreateActive();
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        property.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_rejection_never_bumps_UpdatedAt()
    {
        var property = CreateDraftWithOwnAddress();
        var originalUpdatedAt = property.UpdatedAt;
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        property.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
