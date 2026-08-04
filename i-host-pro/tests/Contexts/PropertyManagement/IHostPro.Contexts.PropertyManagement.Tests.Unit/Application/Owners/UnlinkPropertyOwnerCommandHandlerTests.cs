using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

public class UnlinkPropertyOwnerCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private static readonly Address SomeAddress = Address.Create(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Property CreateProperty() =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, null, SomeAddress, Now);

    private static PropertyOwnerLink CreateLink(Guid propertyId) =>
        PropertyOwnerLink.Create(Guid.NewGuid(), TenantId, propertyId, OwnerUserId, ActorId, Now);

    private sealed record Fixture(
        FakePropertyRepository PropertyRepository,
        FakePropertyOwnerReader OwnerReader,
        FakePropertyOwnerWriter OwnerWriter,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UnlinkPropertyOwnerCommandHandler Handler);

    private static Fixture CreateFixture(Property? property, PropertyOwnerLink? link)
    {
        var propertyRepository = FakePropertyRepository.WithProperty(property);
        var ownerReader = FakePropertyOwnerReader.WithFindResult(link);
        var ownerWriter = new FakePropertyOwnerWriter();
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new UnlinkPropertyOwnerCommandHandler(
            propertyRepository, ownerReader, ownerWriter, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(propertyRepository, ownerReader, ownerWriter, auditWriter, eventCollector, handler);
    }

    private static UnlinkPropertyOwnerCommand Command(Guid propertyId) => new(TenantId, ActorId, propertyId, OwnerUserId);

    [Fact]
    public async Task An_existing_link_is_removed_successfully()
    {
        var property = CreateProperty();
        var link = CreateLink(property.Id);
        var fixture = CreateFixture(property, link);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OwnerWriter.UnlinkedLinks.Should().ContainSingle();
        fixture.OwnerWriter.UnlinkedLinks[0].Should().BeSameAs(link);
    }

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound_and_never_queries_the_link()
    {
        var fixture = CreateFixture(property: null, link: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
        fixture.OwnerReader.LastRequestedPropertyId.Should().BeNull();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_link_that_does_not_exist_fails_with_PropertyOwnerNotLinked_and_performs_no_side_effect()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(property, link: null);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerNotLinked);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Repeating_an_unlink_of_an_already_removed_link_fails_with_PropertyOwnerNotLinked_and_writes_no_second_audit_or_event()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(property, link: null);

        var first = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);
        var second = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerNotLinked);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Unlinking_writes_exactly_one_audit_entry_with_the_property_owner_unlinked_action_code_and_owner_user_id_only()
    {
        var property = CreateProperty();
        var link = CreateLink(property.Id);
        var fixture = CreateFixture(property, link);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Property");
        entry.ActionCode.Should().Be("property_owner_unlinked");
        entry.AggregateId.Should().Be(property.Id);
        entry.ChangedFields.Should().Equal("owner_user_id");
    }

    [Fact]
    public async Task Unlinking_enqueues_exactly_one_PropertyOwnerUnlinked_event()
    {
        var property = CreateProperty();
        var link = CreateLink(property.Id);
        var fixture = CreateFixture(property, link);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyOwnerUnlinked>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(property.Id);
        events[0].AggregateType.Should().Be("Property");
        events[0].PropertyId.Should().Be(property.Id);
        events[0].OwnerUserId.Should().Be(OwnerUserId);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.OwnerWriter.UnlinkedLinks.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
