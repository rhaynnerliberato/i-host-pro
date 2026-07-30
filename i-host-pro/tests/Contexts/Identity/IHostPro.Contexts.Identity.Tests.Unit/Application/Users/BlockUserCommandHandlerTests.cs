using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class BlockUserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static User CreateTargetUser(UserStatus status = UserStatus.Active)
    {
        var hash = PasswordHash.FromEncoded("irrelevant-for-this-test");
        var user = User.Register(Guid.NewGuid(), TenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Target User", hash, Now);
        if (status == UserStatus.Blocked)
            user.Block(Now);
        return user;
    }

    private sealed record Fixture(
        FakeUserRepository UserRepository,
        FakeUserRoleReader RoleReader,
        FakeLastAdministratorGuard AdministratorGuard,
        FakeUserSessionRevoker SessionRevoker,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        BlockUserCommandHandler Handler);

    private static Fixture CreateFixture(
        User? user,
        string[]? currentRoleCodes = null,
        bool anotherActiveAdministratorRemains = true,
        Guid[]? revokedSessionIds = null)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes(currentRoleCodes ?? []);
        var administratorGuard = anotherActiveAdministratorRemains
            ? FakeLastAdministratorGuard.ThatAllows()
            : FakeLastAdministratorGuard.ThatRejects();
        var sessionRevoker = revokedSessionIds is null
            ? FakeUserSessionRevoker.ThatRevokesNone()
            : FakeUserSessionRevoker.ThatRevokes(revokedSessionIds);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new BlockUserCommandHandler(
            userRepository, roleReader, administratorGuard, sessionRevoker, auditWriter, eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(userRepository, roleReader, administratorGuard, sessionRevoker, auditWriter, eventCollector, handler);
    }

    private static BlockUserCommand ValidCommand(Guid targetUserId, Guid? actorId = null) =>
        new(TenantId, actorId ?? ActorId, targetUserId);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task A_valid_block_changes_the_status_audits_it_and_enqueues_the_event_exactly_once()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"]);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Blocked);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.UserBlocked);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);
        // No sensitive/incidental data leaks into the audit entry's free-text field.
        fixture.AuditWriter.RecordedEntries[0].ReasonCode.Should().BeNull();

        var blockedEvents = fixture.EventCollector.EnqueuedEvents.OfType<UserBlocked>().ToArray();
        blockedEvents.Should().ContainSingle();
        blockedEvents[0].AggregateId.Should().Be(user.Id);
        blockedEvents[0].ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task All_active_sessions_are_revoked_with_the_user_blocked_reason()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.SessionRevoker.CallCount.Should().Be(1);
        fixture.SessionRevoker.LastTenantId.Should().Be(TenantId);
        fixture.SessionRevoker.LastUserId.Should().Be(user.Id);
        fixture.SessionRevoker.LastSessionRevocationReasonCode.Should().Be(SessionRevokedReasonCodes.UserBlocked);
        fixture.SessionRevoker.LastRefreshTokenRevocationReason.Should().Be(RefreshTokenRevocationReason.UserBlocked);
    }

    [Fact]
    public async Task One_SessionRevoked_event_is_enqueued_per_revoked_session_chained_to_the_primary_event()
    {
        var user = CreateTargetUser();
        var sessionIdA = Guid.NewGuid();
        var sessionIdB = Guid.NewGuid();
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"], revokedSessionIds: [sessionIdA, sessionIdB]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        var userBlocked = fixture.EventCollector.EnqueuedEvents.OfType<UserBlocked>().Single();
        var sessionRevokedEvents = fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().ToArray();
        sessionRevokedEvents.Should().HaveCount(2);
        sessionRevokedEvents.Select(e => e.SessionId).Should().BeEquivalentTo([sessionIdA, sessionIdB]);
        sessionRevokedEvents.Should().OnlyContain(e => e.CausationId == userBlocked.EventId);
        sessionRevokedEvents.Should().OnlyContain(e => e.ReasonCode == SessionRevokedReasonCodes.UserBlocked);
        sessionRevokedEvents.Should().OnlyContain(e => e.ActorId == ActorId.ToString());
    }

    [Fact]
    public async Task No_SessionRevoked_event_is_enqueued_when_the_user_has_no_active_session()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.OfType<UserBlocked>().Should().ContainSingle();
    }

    [Fact]
    public async Task Blocking_an_Administrator_when_another_active_Administrator_remains_succeeds()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["ADMIN"], anotherActiveAdministratorRemains: true);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.AdministratorGuard.CallCount.Should().Be(1);
        fixture.AdministratorGuard.LastTenantId.Should().Be(TenantId);
        fixture.AdministratorGuard.LastUserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task The_last_Administrator_guard_is_never_consulted_for_a_non_ADMIN_role()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.AdministratorGuard.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Blocking_oneself_when_safe_succeeds()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["ADMIN"], anotherActiveAdministratorRemains: true);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, actorId: user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Blocked);
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
    public async Task Blocking_an_already_blocked_user_fails_with_UserAlreadyBlocked_and_performs_no_side_effect()
    {
        var user = CreateTargetUser(UserStatus.Blocked);
        var fixture = CreateFixture(user, currentRoleCodes: ["OPERATOR"]);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserAlreadyBlocked);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Blocking_the_tenants_last_active_Administrator_fails_with_LastActiveAdministrator_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["ADMIN"], anotherActiveAdministratorRemains: false);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);
        user.Status.Should().Be(UserStatus.Active);
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
        fixture.SessionRevoker.CallCount.Should().Be(0);
    }
}
