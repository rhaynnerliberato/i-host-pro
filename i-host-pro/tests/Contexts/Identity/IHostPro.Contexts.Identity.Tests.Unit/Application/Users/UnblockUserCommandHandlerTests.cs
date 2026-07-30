using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class UnblockUserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static User CreateTargetUser(UserStatus status = UserStatus.Blocked)
    {
        var hash = PasswordHash.FromEncoded("irrelevant-for-this-test");
        var user = User.Register(Guid.NewGuid(), TenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Target User", hash, Now);
        if (status == UserStatus.Blocked)
            user.Block(Now);
        return user;
    }

    private sealed record Fixture(
        FakeUserRepository UserRepository,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UnblockUserCommandHandler Handler);

    private static Fixture CreateFixture(User? user)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new UnblockUserCommandHandler(userRepository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(userRepository, auditWriter, eventCollector, handler);
    }

    private static UnblockUserCommand ValidCommand(Guid targetUserId) => new(TenantId, ActorId, targetUserId);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task A_valid_unblock_changes_the_status_audits_it_and_enqueues_the_event_exactly_once()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Active);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.UserUnblocked);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);
        // No sensitive/incidental data leaks into the audit entry's free-text field.
        fixture.AuditWriter.RecordedEntries[0].ReasonCode.Should().BeNull();

        fixture.EventCollector.EnqueuedEvents.Should().ContainSingle();
        var unblockedEvents = fixture.EventCollector.EnqueuedEvents.OfType<UserUnblocked>().ToArray();
        unblockedEvents.Should().ContainSingle();
        unblockedEvents[0].AggregateId.Should().Be(user.Id);
        unblockedEvents[0].ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task No_SessionRevoked_event_is_ever_enqueued_and_no_session_revoker_is_involved()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().Should().BeEmpty();
    }

    // ---- Rejections -------------------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(user: null);

        var result = await fixture.Handler.Handle(ValidCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Unblocking_an_already_active_user_fails_with_UserAlreadyActive_and_performs_no_side_effect()
    {
        var user = CreateTargetUser(UserStatus.Active);
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserAlreadyActive);
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
