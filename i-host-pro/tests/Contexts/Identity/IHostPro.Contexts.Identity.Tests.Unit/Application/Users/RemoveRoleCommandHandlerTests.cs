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

public class RemoveRoleCommandHandlerTests
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
        FakeLastAdministratorGuard AdministratorGuard,
        FakeUserSessionRevoker SessionRevoker,
        FakeSecurityAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        RemoveRoleCommandHandler Handler);

    private static Fixture CreateFixture(
        User? user,
        IReadOnlyCollection<CatalogRole> roles,
        string[] currentRoleCodes,
        UserRole? foundUserRole,
        bool anotherActiveAdministratorRemains = true,
        Guid[]? revokedSessionIds = null)
    {
        var userRepository = FakeUserRepository.WithUser(user);
        var catalogReader = FakeIdentityCatalogReader.WithRoles(roles);
        var roleReader = FakeUserRoleReader.WithRoleCodesAndFindResult(currentRoleCodes, foundUserRole);
        var roleWriter = new FakeUserRoleWriter();
        var administratorGuard = anotherActiveAdministratorRemains
            ? FakeLastAdministratorGuard.ThatAllows()
            : FakeLastAdministratorGuard.ThatRejects();
        var sessionRevoker = revokedSessionIds is null
            ? FakeUserSessionRevoker.ThatRevokesNone()
            : FakeUserSessionRevoker.ThatRevokes(revokedSessionIds);
        var auditWriter = new FakeSecurityAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();

        var handler = new RemoveRoleCommandHandler(
            userRepository, catalogReader, roleReader, roleWriter, administratorGuard, sessionRevoker, auditWriter,
            eventCollector, new FixedTimeProvider(Now));

        return new Fixture(
            userRepository, catalogReader, roleReader, roleWriter, administratorGuard, sessionRevoker, auditWriter,
            eventCollector, handler);
    }

    private static RemoveRoleCommand ValidCommand(Guid targetUserId, string roleCode = "OPERATOR") =>
        new(TenantId, ActorId, targetUserId, roleCode);

    // ---- Happy path -----------------------------------------------------------

    [Fact]
    public async Task A_valid_removal_removes_the_role_audits_it_and_enqueues_the_event_exactly_once()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "OPERATOR", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR", "HOUSEKEEPER"], userRole);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.RoleWriter.Removed.Should().ContainSingle();
        fixture.RoleWriter.Removed[0].Should().BeSameAs(userRole);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].EventType.Should().Be(SecurityAuditEventType.UserRoleRemoved);
        fixture.AuditWriter.RecordedEntries[0].UserId.Should().Be(user.Id);

        var roleRemovedEvents = fixture.EventCollector.EnqueuedEvents.OfType<UserRoleRemoved>().ToArray();
        roleRemovedEvents.Should().ContainSingle();
        roleRemovedEvents[0].RoleCode.Should().Be("OPERATOR");
        roleRemovedEvents[0].AggregateId.Should().Be(user.Id);
        roleRemovedEvents[0].ActorId.Should().Be(ActorId.ToString());
    }

    [Fact]
    public async Task Removing_ADMIN_when_another_active_Administrator_remains_succeeds()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "ADMIN", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("ADMIN", "Administrador", [])], currentRoleCodes: ["ADMIN", "OPERATOR"], userRole,
            anotherActiveAdministratorRemains: true);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, "ADMIN"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.AdministratorGuard.CallCount.Should().Be(1);
        fixture.AdministratorGuard.LastTenantId.Should().Be(TenantId);
        fixture.AdministratorGuard.LastUserId.Should().Be(user.Id);
        fixture.RoleWriter.Removed.Should().ContainSingle();
    }

    [Fact]
    public async Task The_last_Administrator_guard_is_never_consulted_for_a_non_ADMIN_role()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "OPERATOR", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR", "HOUSEKEEPER"], userRole);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.AdministratorGuard.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task All_active_sessions_are_revoked_with_the_roles_changed_reason()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "OPERATOR", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR", "HOUSEKEEPER"], userRole);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        fixture.SessionRevoker.CallCount.Should().Be(1);
        fixture.SessionRevoker.LastSessionRevocationReasonCode.Should().Be(SessionRevokedReasonCodes.RolesChanged);
        fixture.SessionRevoker.LastRefreshTokenRevocationReason.Should().Be(RefreshTokenRevocationReason.RolesChanged);
    }

    [Fact]
    public async Task One_SessionRevoked_event_is_enqueued_per_revoked_session_chained_to_the_primary_event()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "OPERATOR", Now, ActorId);
        var sessionId = Guid.NewGuid();
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR", "HOUSEKEEPER"], userRole,
            revokedSessionIds: [sessionId]);

        await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        var roleRemoved = fixture.EventCollector.EnqueuedEvents.OfType<UserRoleRemoved>().Single();
        var sessionRevoked = fixture.EventCollector.EnqueuedEvents.OfType<SessionRevoked>().Single();
        sessionRevoked.SessionId.Should().Be(sessionId);
        sessionRevoked.CausationId.Should().Be(roleRemoved.EventId);
        sessionRevoked.ReasonCode.Should().Be(SessionRevokedReasonCodes.RolesChanged);
    }

    // ---- Rejections -------------------------------------------------------------

    [Fact]
    public async Task A_nonexistent_target_user_fails_with_UserNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(user: null, [new CatalogRole("OPERATOR", "Operador", [])], [], null);

        var result = await fixture.Handler.Handle(ValidCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_nonexistent_role_code_fails_with_RoleNotFound_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(user, [new CatalogRole("ADMIN", "Administrador", [])], ["ADMIN"], null);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, "NOT_A_REAL_ROLE"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_role_not_currently_assigned_fails_with_RoleNotAssigned_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", []), new CatalogRole("ADMIN", "Administrador", [])],
            currentRoleCodes: ["ADMIN"], foundUserRole: null);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, "OPERATOR"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.RoleNotAssigned);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Removing_the_users_only_role_fails_with_UserMustHaveAtLeastOneRole_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "OPERATOR", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("OPERATOR", "Operador", [])], currentRoleCodes: ["OPERATOR"], userRole);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserMustHaveAtLeastOneRole);
        AssertNoSideEffect(fixture);
        fixture.AdministratorGuard.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Removing_the_tenants_last_active_Administrator_fails_with_LastActiveAdministrator_and_performs_no_side_effect()
    {
        var user = CreateTargetUser();
        var userRole = new UserRole(TenantId, user.Id, "ADMIN", Now, ActorId);
        var fixture = CreateFixture(
            user, [new CatalogRole("ADMIN", "Administrador", [])], currentRoleCodes: ["ADMIN", "OPERATOR"], userRole,
            anotherActiveAdministratorRemains: false);

        var result = await fixture.Handler.Handle(ValidCommand(user.Id, "ADMIN"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.RoleWriter.Removed.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
        fixture.SessionRevoker.CallCount.Should().Be(0);
    }
}
