using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class ListUsersQueryHandlerTests
{
    private static readonly PagedResult<UserResult> SamplePage = new(1, 20, 0, []);

    [Fact]
    public async Task Page_and_PageSize_are_passed_through_unchanged_when_supplied()
    {
        var reader = FakeUserAdministrationReader.WithPage(SamplePage);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());

        await handler.Handle(new ListUsersQuery(3, 50, null, null), CancellationToken.None);

        reader.LastPage.Should().Be(3);
        reader.LastPageSize.Should().Be(50);
    }

    [Fact]
    public async Task A_missing_page_defaults_to_1()
    {
        var reader = FakeUserAdministrationReader.WithPage(SamplePage);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());

        await handler.Handle(new ListUsersQuery(null, 20, null, null), CancellationToken.None);

        reader.LastPage.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_page_size_defaults_to_the_configured_default()
    {
        var reader = FakeUserAdministrationReader.WithPage(SamplePage);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider(defaultPageSize: 35));

        await handler.Handle(new ListUsersQuery(1, null, null, null), CancellationToken.None);

        reader.LastPageSize.Should().Be(35);
    }

    [Fact]
    public async Task Search_and_status_are_passed_through_unchanged()
    {
        var reader = FakeUserAdministrationReader.WithPage(SamplePage);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());

        await handler.Handle(new ListUsersQuery(1, 20, "alice", UserStatus.Blocked), CancellationToken.None);

        reader.LastSearch.Should().Be("alice");
        reader.LastStatus.Should().Be(UserStatus.Blocked);
    }

    [Fact]
    public async Task An_empty_page_is_a_valid_success_result()
    {
        var reader = FakeUserAdministrationReader.WithPage(new PagedResult<UserResult>(1, 20, 0, []));
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());

        var result = await handler.Handle(new ListUsersQuery(1, 20, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task The_readers_result_and_total_are_propagated_unchanged()
    {
        var user = new UserResult(Guid.NewGuid(), "Test User", "test@ihostpro.com", "Active", ["ADMIN"], DateTimeOffset.UtcNow, null);
        var page = new PagedResult<UserResult>(2, 10, 37, [user]);
        var reader = FakeUserAdministrationReader.WithPage(page);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());

        var result = await handler.Handle(new ListUsersQuery(2, 10, null, null), CancellationToken.None);

        result.Value.Should().BeEquivalentTo(page);
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_reader()
    {
        var reader = FakeUserAdministrationReader.WithPage(SamplePage);
        var handler = new ListUsersQueryHandler(reader, new FakeUserListingSettingsProvider());
        using var cts = new CancellationTokenSource();

        await handler.Handle(new ListUsersQuery(1, 20, null, null), cts.Token);

        reader.LastCancellationToken.Should().Be(cts.Token);
    }
}
