using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class GetUserByIdQueryValidatorTests
{
    private static readonly GetUserByIdQueryValidator Validator = new();

    [Fact]
    public void Validate_succeeds_for_a_well_formed_query()
    {
        Validator.Validate(new GetUserByIdQuery(Guid.NewGuid())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_an_empty_user_id()
    {
        var result = Validator.Validate(new GetUserByIdQuery(Guid.Empty));

        result.Errors.Should().Contain(e => e.ErrorCode == "user_id_required");
    }
}
