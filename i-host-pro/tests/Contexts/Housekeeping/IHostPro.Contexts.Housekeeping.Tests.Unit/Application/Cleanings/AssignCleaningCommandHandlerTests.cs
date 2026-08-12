using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Tests.Unit.Infrastructure;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class AssignCleaningCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid HousekeeperUserId = Guid.NewGuid();

    private sealed record Fixture(
        FakeCleaningRepository Repository,
        FakeHousekeepingAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        FakeIdentityUserEligibilityReader EligibilityReader,
        AssignCleaningCommandHandler Handler);

    private static Fixture CreateFixture(Cleaning? cleaning, IdentityUserEligibility? eligibility)
    {
        var repository = FakeCleaningRepository.WithCleaning(cleaning);
        var auditWriter = new FakeHousekeepingAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var eligibilityReader = FakeIdentityUserEligibilityReader.With(eligibility);
        var handler = new AssignCleaningCommandHandler(
            eligibilityReader,
            new PassThroughCleaningTransitionExecutor(),
            repository,
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, eligibilityReader, handler);
    }

    private static Cleaning PendingCleaning() =>
        Cleaning.Create(Guid.NewGuid(), TenantId, Guid.NewGuid(), null, Guid.NewGuid(), Now.AddMinutes(-10));

    private static AssignCleaningCommand Command(Guid cleaningId) =>
        new(TenantId, ActorId, cleaningId, HousekeeperUserId);

    [Fact]
    public async Task An_eligible_housekeeper_transitions_the_cleaning_to_Assigned()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: true));

        var result = await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Assigned");
        result.Value.AssignedHousekeeperUserId.Should().Be(HousekeeperUserId);
    }

    [Fact]
    public async Task The_eligibility_check_requests_the_HOUSEKEEPER_role_code()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: true));

        await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        fixture.EligibilityReader.LastRequiredRoleCode.Should().Be(IdentityRoleCodes.Housekeeper);
    }

    [Fact]
    public async Task A_nonexistent_user_ie_null_eligibility_fails_with_HousekeeperNotEligible_and_performs_no_side_effect()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, eligibility: null);

        var result = await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.HousekeeperNotEligible);
        AssertNoSideEffect(fixture, cleaning);
    }

    [Fact]
    public async Task An_inactive_user_fails_with_HousekeeperNotEligible()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: false, HasRequiredRole: true));

        var result = await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.HousekeeperNotEligible);
        AssertNoSideEffect(fixture, cleaning);
    }

    [Fact]
    public async Task A_user_without_the_HOUSEKEEPER_role_fails_with_HousekeeperNotEligible()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: false));

        var result = await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.HousekeeperNotEligible);
        AssertNoSideEffect(fixture, cleaning);
    }

    [Fact]
    public async Task A_nonexistent_cleaning_fails_with_CleaningNotFound()
    {
        var fixture = CreateFixture(null, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: true));

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.CleaningNotFound);
    }

    [Fact]
    public async Task Assigning_a_cleaning_that_is_not_Pending_fails_with_InvalidCleaningTransition()
    {
        var cleaning = PendingCleaning();
        cleaning.Assign(Guid.NewGuid(), Now.AddMinutes(-5));
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: true));

        var result = await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(HousekeepingErrorCodes.InvalidCleaningTransition);
    }

    [Fact]
    public async Task Assignment_writes_exactly_one_audit_entry_and_enqueues_exactly_one_CleaningAssigned_event()
    {
        var cleaning = PendingCleaning();
        var fixture = CreateFixture(cleaning, new IdentityUserEligibility(HousekeeperUserId, IsActive: true, HasRequiredRole: true));

        await fixture.Handler.Handle(Command(cleaning.Id), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].ActionCode.Should().Be("cleaning_assigned");

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CleaningAssigned>().ToArray();
        events.Should().ContainSingle();
        events[0].HousekeeperUserId.Should().Be(HousekeeperUserId);
    }

    private static void AssertNoSideEffect(Fixture fixture, Cleaning cleaning)
    {
        cleaning.Status.Should().Be(CleaningStatus.Pending);
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
