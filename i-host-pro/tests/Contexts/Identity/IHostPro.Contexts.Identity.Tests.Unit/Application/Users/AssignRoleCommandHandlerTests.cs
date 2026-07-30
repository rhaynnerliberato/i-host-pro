using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Application.Catalog;
using IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class AssignRoleCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static User CreateTargetUser()
    {
        var hash = PasswordHash.FromEncoded("irrelevant-for-this-test");
        return User.Register(Guid.NewGuid(), TenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Target User", hash, Now);
    }

    private sealed record Fixture(
        FakeUserRepository UserRepository,
        FakeIdentityCatalogReader CatalogReader,
        FakeUserRoleReader RoleReader,
        FakeUserRoleWriter RoleWriter,
        FakeUserSessionRevoker SessionRevoker,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        AssignRoleCommandHandler Handler);

    private static Fixture CreateFixture(
        User? user, IReadOnlyCollection<CatalogRole> roles, string[]? currentRoleCodes = null, Guid[]? revokedSessionIds = null)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var catalogReader = FakeIdentityCatalogReader.WithRoles(roles);
        var roleReader = FakeUserRoleReader.WithRoleCodes(currentRoleCodes ?? []);
        var roleWriter = new FakeUserRoleWriter();
        var sessionRevoker = revokedSessionIds is null
            ? FakeUserSessionRevoker.ThatRevokesNone()
            : FakeUserSessionRevoker.ThatRevokes(revokedSessionIds);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new AssignRoleCommandHandler(
            userRepository, catalogReader, roleReader, roleWriter, sessionRevoker, auditWriter, eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(userRepository, catalogReader, roleReader, roleWriter, sessionRevoker, auditWriter, eventCollector, handler);
    }

    private static AssignRoleCommand ValidCommand(Guid targetUserId, string roleCode = "OPERATOR") =>
        new(TenantId, ActorId, targetUserId, roleCode);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task A_valid_assignment_persists_the_role_audits_it_and_enqueues_the_event_exactly_once()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, [new CatalogRole("OPERATOR", "Operador", [])]);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.RoleWriter.Assigned.Should().ContainSingle();
        fixture.RoleWriter.Assigned[0].UserId.Should().Be(user.Id);
        fixture.RoleWriter.Assigned[0].RoleCode.Should().Be("OPERATOR");
        fixture.RoleWriter.Assigned[0].AssignedByUserId.Should().Be(ActorId);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.UserRoleAssigned);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);
        // No sensitive/incidental data leaks into the audit entry's free-text field.
        fixture.AuditWriter.RecordedEntries[0].ReasonCode.Should().BeNull();

        var roleAssignedEvents = fixture.EventCollector.EnqueuedEvents.OfType<UserRoleAssigned>().ToArray();
        roleAssignedEvents.Should().ContainSingle();
        roleAssignedEvents[0].RoleCode.Should().Be("OPERATOR");
        roleAssignedEvents[0].AggregateId.Should().Be(user.Id);
        roleAssignedEvents[0].ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task All_active_sessions_are_revoked_with_the_roles_changed_reason()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, [new CatalogRole("OPERATOR", "Operador", [])]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.SessionRevoker.CallCount.Should().Be(1);
        fixture.SessionRevoker.LastTenantId.Should().Be(TenantId);
        fixture.SessionRevoker.LastUserId.Should().Be(user.Id);
        fixture.SessionRevoker.LastSessionRevocationReasonCode.Should().Be(SessionRevokedReasonCodes.RolesChanged);
        fixture.SessionRevoker.LastRefreshTokenRevocationReason.Should().Be(RefreshTokenRevocationReason.RolesChanged);
    }

    [Fact]
    public async Task One_SessionRevoked_event_is_enqueued_per_revoked_session_chained_to_the_primary_event()
    {
        var user = CreateTargetUser();
        var sessionIdA = Guid.NewGuid();
        var sessionIdB = Guid.NewGuid();
        var fixture = CreateFixture(user, [new CatalogRole("OPERATOR", "Operador", [])], revokedSessionIds: [sessionIdA, sessionIdB]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        var roleAssigned = fixture.EventCollector.EnqueuedEvents.OfType<UserRoleAssigned>().Single();
        var sessionRevokedEvents = fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().ToArray();
        sessionRevokedEvents.Should().HaveCount(2);
        sessionRevokedEvents.Select(e => e.SessionId).Should().BeEquivalentTo([sessionIdA, sessionIdB]);
        sessionRevokedEvents.Should().OnlyContain(e => e.CausationId == roleAssigned.EventId);
        sessionRevokedEvents.Should().OnlyContain(e => e.ReasonCode == SessionRevokedReasonCodes.RolesChanged);
        sessionRevokedEvents.Should().OnlyContain(e => e.ActorId == ActorId.ToString());
    }

    [Fact]
    public async Task No_SessionRevoked_event_is_enqueued_when_the_user_has_no_active_session()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, [new CatalogRole("OPERATOR", "Operador", [])]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.OfType<UserRoleAssigned>().Should().ContainSingle();
    }

    // ---- Rejections -------------------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(user: null, [new CatalogRole("OPERATOR", "Operador", [])]);

        var result = await fixture.Handler.Handle(ValidCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_nonexistent_role_code_fails_with_RoleNotFound_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, [new CatalogRole("ADMIN", "Administrador", [])]);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, "NOT_A_REAL_ROLE"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_role_already_assigned_fails_with_RoleAlreadyAssigned_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR"]);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleAlreadyAssigned);
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.RoleWriter.Assigned.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
        fixture.SessionRevoker.CallCount.Should().Be(0);
    }
}
