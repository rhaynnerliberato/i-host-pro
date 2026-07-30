using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class ListUsersQueryValidatorTests
{
    private static ListUsersQueryValidator NewValidator(int maxPageSize = 100) =>
        new(new FakeUserListingSettingsProvider(maxPageSize: maxPageSize));

    [Fact]
    public void Validate_succeeds_when_page_and_page_size_are_omitted()
    {
        var result = NewValidator().Validate(new ListUsersQuery(null, null, null, null));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_a_page_less_than_one()
    {
        var result = NewValidator().Validate(new ListUsersQuery(0, 20, null, null));

        result.Errors.Should().Contain(e => e.ErrorCode == "page_must_be_positive");
    }

    [Fact]
    public void Validate_fails_for_a_page_size_less_than_one()
    {
        var result = NewValidator().Validate(new ListUsersQuery(1, 0, null, null));

        result.Errors.Should().Contain(e => e.ErrorCode == "page_size_out_of_range");
    }

    [Fact]
    public void Validate_fails_for_a_page_size_above_the_configured_maximum()
    {
        var result = NewValidator(maxPageSize: 100).Validate(new ListUsersQuery(1, 101, null, null));

        result.Errors.Should().Contain(e => e.ErrorCode == "page_size_out_of_range");
    }

    [Fact]
    public void Validate_succeeds_for_a_page_size_exactly_at_the_configured_maximum()
    {
        var result = NewValidator(maxPageSize: 100).Validate(new ListUsersQuery(1, 100, null, null));

        result.IsValid.Should().BeTrue();
    }
}
