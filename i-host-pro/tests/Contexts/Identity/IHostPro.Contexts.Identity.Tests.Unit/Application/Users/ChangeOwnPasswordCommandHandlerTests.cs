using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class ChangeOwnPasswordCommandHandlerTests
{
    private const string CurrentPassword = "Correct-Horse-Battery-42!";
    private const string NewPassword = "New-Correct-Horse-Battery-43!";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();

    private static User CreateUser(UserStatus status = UserStatus.Active)
    {
        var hash = PasswordHash.FromEncoded("irrelevant-for-this-test");
        var user = User.Register(Guid.NewGuid(), TenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, Now);
        if (status == UserStatus.Blocked)
            user.Block(Now);
        return user;
    }

    private sealed record Fixture(
        FakeUserRepository UserRepository,
        FakeUserAuthenticationServiceForPasswordChecks AuthenticationService,
        FakeUserProvisioningService ProvisioningService,
        FakeUserSessionRevoker SessionRevoker,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        ChangeOwnPasswordCommandHandler Handler);

    private static Fixture CreateFixture(
        User? user,
        bool passwordPolicyAccepts = true,
        Guid[]? revokedSessionIds = null)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var authenticationService = FakeUserAuthenticationServiceForPasswordChecks.WithCurrentPassword(CurrentPassword);
        var provisioningService = passwordPolicyAccepts
            ? FakeUserProvisioningService.ThatAccepts()
            : FakeUserProvisioningService.ThatRejects("PasswordTooShort");
        var sessionRevoker = revokedSessionIds is null
            ? FakeUserSessionRevoker.ThatRevokesNone()
            : FakeUserSessionRevoker.ThatRevokes(revokedSessionIds);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new ChangeOwnPasswordCommandHandler(
            userRepository, authenticationService, provisioningService, sessionRevoker, auditWriter, eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(userRepository, authenticationService, provisioningService, sessionRevoker, auditWriter, eventCollector, handler);
    }

    private static ChangeOwnPasswordCommand Command(Guid userId, string? currentPassword = null, string? newPassword = null) =>
        new(TenantId, userId, currentPassword ?? CurrentPassword, newPassword ?? NewPassword);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task A_valid_change_hashes_the_new_password_and_audits_it()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.ProvisioningService.LastHashedPassword.Should().Be(NewPassword);
        user.PasswordHash.Value.Should().Be($"hashed:{NewPassword}");

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.PasswordChangedBySelf);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task Exactly_one_PasswordChanged_event_is_enqueued_with_ChangeType_self_and_the_users_own_ActorId()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PasswordChanged>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangeType.Should().Be(PasswordChangeTypeCodes.Self);
        events[0].AggregateId.Should().Be(user.Id);
        events[0].ActorId.Should().Be(user.Id.ToString());
    }

    [Fact]
    public async Task The_hash_is_computed_only_once()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        fixture.ProvisioningService.HashPasswordCallCount.Should().Be(1);
    }

    [Fact]
    public async Task All_active_sessions_are_revoked_with_the_password_changed_reason_including_the_current_one()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        fixture.SessionRevoker.CallCount.Should().Be(1);
        fixture.SessionRevoker.LastTenantId.Should().Be(TenantId);
        fixture.SessionRevoker.LastUserId.Should().Be(user.Id);
        fixture.SessionRevoker.LastSessionRevocationReasonCode.Should().Be(SessionRevokedReasonCodes.PasswordChanged);
        fixture.SessionRevoker.LastRefreshTokenRevocationReason.Should().Be(RefreshTokenRevocationReason.PasswordChanged);
    }

    [Fact]
    public async Task One_SessionRevoked_event_is_enqueued_per_revoked_session_chained_to_the_primary_event()
    {
        var user = CreateUser();
        var sessionIdA = Guid.NewGuid();
        var sessionIdB = Guid.NewGuid();
        var fixture = CreateFixture(user, revokedSessionIds: [sessionIdA, sessionIdB]);

        await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        var passwordChanged = fixture.EventCollector.EnqueuedEvents.OfType<PasswordChanged>().Single();
        var sessionRevokedEvents = fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().ToArray();
        sessionRevokedEvents.Should().HaveCount(2);
        sessionRevokedEvents.Select(e => e.SessionId).Should().BeEquivalentTo([sessionIdA, sessionIdB]);
        sessionRevokedEvents.Should().OnlyContain(e => e.CausationId == passwordChanged.EventId);
        sessionRevokedEvents.Should().OnlyContain(e => e.ReasonCode == SessionRevokedReasonCodes.PasswordChanged);
        sessionRevokedEvents.Should().OnlyContain(e => e.ActorId == user.Id.ToString());
    }

    [Fact]
    public async Task No_SessionRevoked_event_is_enqueued_when_the_user_has_no_active_session()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.OfType<PasswordChanged>().Should().ContainSingle();
    }

    // ---- Rejections -------------------------------------------------------------

    [Fact]
    public async Task A_nonexistent_authenticated_user_fails_with_AuthenticatedUserNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(user: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.AuthenticatedUserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_blocked_authenticated_user_fails_with_AuthenticatedUserNotFound_and_performs_no_side_effect()
    {
        var user = CreateUser(UserStatus.Blocked);
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.AuthenticatedUserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_incorrect_current_password_fails_with_InvalidCurrentPassword_and_performs_no_side_effect()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, currentPassword: "wrong-password"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.InvalidCurrentPassword);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_new_password_outside_the_policy_fails_with_the_policy_violation_and_performs_no_side_effect()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user, passwordPolicyAccepts: false);

        var result = await fixture.Handler.Handle(Command(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PasswordTooShort");
        fixture.ProvisioningService.HashPasswordCallCount.Should().Be(0);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_new_password_equal_to_the_current_one_fails_with_NewPasswordMustDiffer_and_performs_no_side_effect()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, newPassword: CurrentPassword), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.NewPasswordMustDiffer);
        fixture.ProvisioningService.HashPasswordCallCount.Should().Be(0);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task The_current_password_is_verified_before_the_new_password_policy_is_checked()
    {
        var user = CreateUser();
        var fixture = CreateFixture(user, passwordPolicyAccepts: false);

        var result = await fixture.Handler.Handle(Command(user.Id, currentPassword: "wrong-password"), CancellationToken.None);

        result.Error.Code.Should().Be(IdentityErrorCodes.InvalidCurrentPassword);
        fixture.ProvisioningService.ValidatePasswordCallCount.Should().Be(0); // never reached
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
        fixture.SessionRevoker.CallCount.Should().Be(0);
    }
}
