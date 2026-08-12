using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class CreateCleaningCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private sealed record Fixture(
        FakeCleaningRepository Repository,
        FakeHousekeepingAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        CreateCleaningCommandHandler Handler);

    private static Fixture CreateFixture(bool propertyIsKnownActive = true, bool reservationExists = true)
    {
        var repository = FakeCleaningRepository.WithCleaning(null);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CreateCleaningCommandHandler(
            FakePropertyReferenceProjection.With(propertyIsKnownActive),
            FakeReservationReferenceProjection.With(reservationExists),
            new PassThroughHousekeepingTransactionExecutor(),
            repository,
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, handler);
    }

    private static CreateCleaningCommand Command(Guid? reservationId = null) =>
        new(TenantId, ActorId, PropertyId, reservationId);

    [Fact]
    public async Task A_valid_request_creates_the_cleaning_as_Pending()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Pending");
        result.Value.PropertyId.Should().Be(PropertyId);
        fixture.Repository.AddedCleanings.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_or_inactive_property_fails_with_PropertyNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(propertyIsKnownActive: false);

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.PropertyNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_reservationId_not_present_in_the_local_projection_fails_with_ReservationReferenceNotAvailable()
    {
        var fixture = CreateFixture(reservationExists: false);

        var result = await fixture.Handler.Handle(Command(reservationId: Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.ReservationReferenceNotAvailable);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task No_reservationId_supplied_never_checks_the_reservation_projection()
    {
        var fixture = CreateFixture(reservationExists: false);

        var result = await fixture.Handler.Handle(Command(reservationId: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReservationId.Should().BeNull();
    }

    [Fact]
    public async Task Creation_writes_exactly_one_audit_entry_with_the_cleaning_created_action_code_and_empty_ChangedFields()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Cleaning");
        entry.ActionCode.Should().Be("cleaning_created");
        entry.AggregateId.Should().Be(result.Value.Id);
        entry.ChangedFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Creation_enqueues_exactly_one_CleaningCreated_event_with_the_stable_Pending_status_code()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CleaningCreated>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(result.Value.Id);
        events[0].AggregateType.Should().Be("Cleaning");
        events[0].PropertyId.Should().Be(PropertyId);
        events[0].Status.Should().Be("Pending");
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.Repository.AddedCleanings.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
