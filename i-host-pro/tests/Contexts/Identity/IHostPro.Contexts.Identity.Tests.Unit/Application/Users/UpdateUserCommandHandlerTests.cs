using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class UpdateUserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static User CreateTargetUser(string fullName = "Original Name", string email = "original@ihostpro.com")
    {
        var hash = PasswordHash.FromEncoded("irrelevant-for-this-test");
        return User.Register(Guid.NewGuid(), TenantId, Email.Create(email), fullName, hash, Now);
    }

    private sealed record Fixture(
        FakeUserRepository UserRepository,
        FakeUserRoleReader RoleReader,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UpdateUserCommandHandler Handler);

    private static Fixture CreateFixture(User? user, string[]? currentRoleCodes = null)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes(currentRoleCodes ?? ["OPERATOR"]);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new UpdateUserCommandHandler(userRepository, roleReader, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(userRepository, roleReader, auditWriter, eventCollector, handler);
    }

    private static UpdateUserCommand Command(Guid targetUserId, string? fullName = null, string? email = null) =>
        new(TenantId, ActorId, targetUserId, fullName, email);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task Changing_only_the_full_name_updates_it_audits_and_enqueues_ChangedFields_full_name_only()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, fullName: "New Name"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FullName.Should().Be("New Name");
        user.FullName.Should().Be("New Name");
        user.Email.Value.Should().Be("original@ihostpro.com"); // untouched

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.UserUpdated);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<UserUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("full_name");
        events[0].AggregateId.Should().Be(user.Id);
        events[0].ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task Changing_only_the_email_updates_it_and_enqueues_ChangedFields_email_only()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, email: "new@ihostpro.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Email.Should().Be("new@ihostpro.com");
        user.FullName.Should().Be("Original Name"); // untouched

        var events = fixture.EventCollector.EnqueuedEvents.OfType<UserUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("email");
    }

    [Fact]
    public async Task Changing_both_fields_enqueues_a_single_event_with_both_ChangedFields_in_order()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(
            Command(user.Id, fullName: "New Name", email: "new@ihostpro.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var events = fixture.EventCollector.EnqueuedEvents.OfType<UserUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("full_name", "email"); // deterministic order
        fixture.AuditWriter.RecordedEntries.Should().ContainSingle(); // one audit entry, not two
    }

    [Fact]
    public async Task The_response_reflects_the_users_current_roles()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, currentRoleCodes: ["ADMIN", "OPERATOR"]);

        var result = await fixture.Handler.Handle(Command(user.Id, fullName: "New Name"), CancellationToken.None);

        result.Value.Roles.Should().BeEquivalentTo(["ADMIN", "OPERATOR"]);
    }

    // ---- Idempotency (Section 4) --------------------------------------------

    [Fact]
    public async Task Supplying_the_same_full_name_is_a_no_op()
    {
        var user = CreateTargetUser(fullName: "Original Name");
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, fullName: "Original Name"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_email_with_different_casing_is_a_no_op()
    {
        var user = CreateTargetUser(email: "same@ihostpro.com");
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, email: "SAME@IHostPro.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_both_fields_already_equal_to_the_current_values_is_a_no_op()
    {
        var user = CreateTargetUser(fullName: "Original Name", email: "original@ihostpro.com");
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(
            Command(user.Id, fullName: "Original Name", email: "original@ihostpro.com"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_no_op_request_never_bumps_UpdatedAt()
    {
        var user = CreateTargetUser(fullName: "Original Name");
        var originalUpdatedAt = user.UpdatedAt;
        var fixture = CreateFixture(user);

        await fixture.Handler.Handle(Command(user.Id, fullName: "Original Name"), CancellationToken.None);

        user.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    // ---- Rejections -------------------------------------------------------------

    [Fact]
    public async Task Neither_field_supplied_fails_with_NoChangesProvided_and_never_touches_the_repository()
    {
        var fixture = CreateFixture(user: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.NoChangesProvided);
        fixture.UserRepository.GetByIdCallCount.Should().Be(0); // rejected before any lookup
    }

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(user: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid(), fullName: "New Name"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_whitespace_only_email_fails_with_email_invalid_format_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, email: "   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("email_invalid_format");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_malformed_email_fails_with_email_invalid_format_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user);

        var result = await fixture.Handler.Handle(Command(user.Id, email: "not-an-email"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("email_invalid_format");
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
