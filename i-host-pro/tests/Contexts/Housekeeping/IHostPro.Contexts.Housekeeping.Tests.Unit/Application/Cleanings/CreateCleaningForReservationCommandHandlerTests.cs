using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

/// <summary>
/// Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): covers the two
/// guards this automated flow needs that the HTTP flow
/// (<see cref="CreateCleaningCommandHandlerTests"/>) does not — idempotency
/// and the best-effort cancellation guard — plus the automated-actor
/// shape (<c>CreatedByUserId</c>/<c>ScheduledAtUtc</c> both always null).
/// </summary>
public class CreateCleaningForReservationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private sealed record Fixture(
        FakeCleaningRepository Repository,
        FakeHousekeepingAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        FakeCleaningReader CleaningReader,
        CreateCleaningForReservationCommandHandler Handler);

    private static Fixture CreateFixture(
        bool propertyIsKnownActive = true, bool alreadyCreatedAutomated = false, bool reservationIsCancelled = false)
    {
        var repository = FakeCleaningRepository.WithCleaning(null);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var cleaningReader = FakeCleaningReader.WithDetail(null);
        cleaningReader.ExistsAutomated = alreadyCreatedAutomated;

        var handler = new CreateCleaningForReservationCommandHandler(
            FakePropertyReferenceProjection.With(propertyIsKnownActive),
            FakeReservationReferenceProjection.With(exists: true, isCancelled: reservationIsCancelled),
            cleaningReader,
            new PassThroughHousekeepingTransactionExecutor(),
            repository,
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, cleaningReader, handler);
    }

    private static CreateCleaningForReservation Command() => new()
    {
        TenantId = TenantId,
        ReservationId = ReservationId,
        PropertyId = PropertyId,
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    [Fact]
    public async Task A_valid_command_creates_the_cleaning_with_no_actor_and_no_schedule()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        fixture.Repository.AddedCleanings.Should().ContainSingle();
        var cleaning = fixture.Repository.AddedCleanings[0];
        cleaning.PropertyId.Should().Be(PropertyId);
        cleaning.ReservationId.Should().Be(ReservationId);
        cleaning.CreatedByUserId.Should().BeNull("this flow has no authenticated actor (ADR-018) — no seeded system user");
        cleaning.ScheduledAtUtc.Should().BeNull("ScheduledAtUtc is never derived from the checkout date this checkpoint (ADR-018 — belongs to Fase 10)");
    }

    [Fact]
    public async Task Creation_enqueues_CleaningCreated_with_System_actor_and_no_ActorId()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CleaningCreated>().ToArray();
        events.Should().ContainSingle();
        events[0].ActorType.Should().Be("System");
        events[0].ActorId.Should().BeNull();
        events[0].Status.Should().Be("Pending");
        events[0].ScheduledAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Creation_writes_exactly_one_audit_entry_with_no_actor()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.ActorUserId.Should().BeNull();
        entry.ActionCode.Should().Be("cleaning_created_by_workflow");
    }

    [Fact]
    public async Task An_already_created_automated_cleaning_is_a_no_op_idempotency_layer_1()
    {
        var fixture = CreateFixture(alreadyCreatedAutomated: true);

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_reservation_already_marked_cancelled_by_the_local_projection_is_a_no_op_best_effort_guard()
    {
        var fixture = CreateFixture(reservationIsCancelled: true);

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_unknown_or_inactive_property_throws_relying_on_Wolverines_default_redelivery()
    {
        var fixture = CreateFixture(propertyIsKnownActive: false);

        var act = async () => await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.Repository.AddedCleanings.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
