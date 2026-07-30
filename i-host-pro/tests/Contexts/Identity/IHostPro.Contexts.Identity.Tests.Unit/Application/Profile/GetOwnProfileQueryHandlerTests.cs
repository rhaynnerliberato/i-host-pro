using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Profile;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

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
        var handler = new GetOwnProfileQueryHandler(authService, roleReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(user.Id);
        result.Value.FullName.Should().Be(user.FullName);
        result.Value.Email.Should().Be(user.Email.Value);
        result.Value.Status.Should().Be(user.Status.ToString());
        result.Value.CreatedAt.Should().Be(user.CreatedAt);
        result.Value.LastLoginAt.Should().Be(user.LastLoginAt);
    }

    [Fact]
    public async Task A_nonexistent_user_produces_a_failure_never_an_exception()
    {
        var authService = FakeUserAuthenticationService.WithNoUser();
        var roleReader = FakeUserRoleReader.WithRoleCodes();
        var handler = new GetOwnProfileQueryHandler(authService, roleReader);

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
        var handler = new GetOwnProfileQueryHandler(authService, roleReader);

        var result = await handler.Handle(new GetOwnProfileQuery(user.Id), CancellationToken.None);

        result.Value.Roles.Should().Equal("ADMIN", "HOUSEKEEPER", "PROPERTY_OWNER");
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
        var handler = new GetOwnProfileQueryHandler(authService, roleReader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new GetOwnProfileQuery(user.Id), cts.Token);

        roleReader.LastCancellationToken.Should().Be(cts.Token);
    }
}
