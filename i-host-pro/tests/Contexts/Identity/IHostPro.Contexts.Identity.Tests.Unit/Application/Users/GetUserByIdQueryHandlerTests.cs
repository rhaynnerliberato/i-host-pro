using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class GetUserByIdQueryHandlerTests
{
    [Fact]
    public async Task An_existing_user_produces_a_successful_result_with_the_readers_data_unchanged()
    {
        var userId = Guid.NewGuid();
        var user = new UserResult(userId, "Test User", "test@ihostpro.com", "Active", ["ADMIN", "OPERATOR"], DateTimeOffset.UtcNow, null);
        var reader = FakeUserAdministrationReader.WithUser(user);
        var handler = new GetUserByIdQueryHandler(reader);

        var result = await handler.Handle(new GetUserByIdQuery(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(user);
        reader.LastUserId.Should().Be(userId);
    }

    [Fact]
    public async Task A_nonexistent_user_produces_a_UserNotFound_failure()
    {
        var reader = FakeUserAdministrationReader.WithUser(null);
        var handler = new GetUserByIdQueryHandler(reader);

        var result = await handler.Handle(new GetUserByIdQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(IdentityErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_reader()
    {
        var reader = FakeUserAdministrationReader.WithUser(null);
        var handler = new GetUserByIdQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new GetUserByIdQuery(Guid.NewGuid()), cts.Token);

        reader.LastCancellationToken.Should().Be(cts.Token);
    }
}
