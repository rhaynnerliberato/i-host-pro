using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Profile;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Tests.Unit.Api;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Profile;

public class GetOwnProfileQueryHandlerTests
{
    private static User NewUser() => User.Register(
        Guid.NewGuid(), Guid.NewGuid(), Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User",
        PasswordHash.FromEncoded("fake-encoded-hash"), DateTimeOffset.UtcNow);

    [Fact]
    public async Task An_existing_user_produces_a_successful_result_with_their_data()
    {
        var user = NewUser();
        var authService = FakeUserAuthenticationService.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes("ADMIN");
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = ["USERS:MANAGE"],
        });
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(user.Id);
        result.Value.FullName.Should().Be(user.FullName);
        result.Value.Email.Should().Be(user.Email.Value);
        result.Value.Status.Should().Be(user.Status.ToString());
        result.Value.Permissions.Should().Equal("USERS:MANAGE");
        result.Value.CreatedAt.Should().Be(user.CreatedAt);
        result.Value.LastLoginAt.Should().Be(user.LastLoginAt);
    }

    [Fact]
    public async Task A_nonexistent_user_produces_a_failure_never_an_exception()
    {
        var authService = FakeUserAuthenticationService.WithNoUser();
        var roleReader = FakeUserRoleReader.WithRoleCodes();
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);

        var result = await handler.Handle(new GetOwnProfileQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IHostPro.Contexts.Identity.Application.Errors.IdentityErrorCodes.AuthenticatedUserNotFound);
    }

    [Fact]
    public async Task Roles_are_returned_sorted_ordinally_regardless_of_the_readers_own_order()
    {
        var user = NewUser();
        var authService = FakeUserAuthenticationService.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes("PROPERTY_OWNER", "ADMIN", "HOUSEKEEPER");
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.Value.Roles.Should().Equal("ADMIN", "HOUSEKEEPER", "PROPERTY_OWNER");
    }

    [Fact]
    public async Task Permissions_are_the_deduplicated_ordinally_sorted_union_of_every_roles_grants()
    {
        var user = NewUser();
        var authService = FakeUserAuthenticationService.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes("OPERATOR", "ADMIN");
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = ["USERS:MANAGE", "AUDIT:READ"],
            ["OPERATOR"] = ["AUDIT:READ", "RESERVATIONS:MANAGE"], // AUDIT:READ overlaps ADMIN's grant
        });
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.Value.Permissions.Should().Equal("AUDIT:READ", "RESERVATIONS:MANAGE", "USERS:MANAGE");
    }

    [Fact]
    public async Task A_role_granting_no_permission_yields_an_empty_permissions_collection()
    {
        var user = NewUser();
        var authService = FakeUserAuthenticationService.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes("SYSTEM");
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["SYSTEM"] = [],
        });
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.Value.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_result_type_exposes_no_sensitive_or_internal_security_field()
    {
        // Structural guard: OwnProfileResult must never grow a property that
        // could leak PasswordHash/NormalizedEmail/SecurityStamp/
        // FailedAccessCount/LockoutEnd (Incremento 3, Checkpoint 4, Section 2).
        var propertyNames = typeof(OwnProfileResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.Should().NotContain([
            "PasswordHash", "NormalizedEmail", "SecurityStamp", "FailedAccessCount", "LockoutEnd",
        ]);
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_role_reader()
    {
        var user = NewUser();
        var authService = FakeUserAuthenticationService.WithUser(user);
        var roleReader = FakeUserRoleReader.WithRoleCodes();
        var permissionReader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var handler = new GetOwnProfileQueryHandler(authService, roleReader, permissionReader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new GetOwnProfileQuery(user.Id), cts.Token);

        roleReader.LastCancellationToken.Should().Be(cts.Token);
    }
}
