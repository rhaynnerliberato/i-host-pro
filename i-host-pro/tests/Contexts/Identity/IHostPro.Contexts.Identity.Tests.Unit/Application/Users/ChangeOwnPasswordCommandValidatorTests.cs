using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class ChangeOwnPasswordCommandValidatorTests
{
    private static readonly ChangeOwnPasswordCommandValidator Validator = new();

    private static ChangeOwnPasswordCommand ValidCommand() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "current-password", "new-password");

    [Fact]
    public void Validate_succeeds_for_a_well_formed_command()
    {
        Validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_an_empty_tenant_id()
    {
        var result = Validator.Validate(ValidCommand() with { TenantId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_user_id()
    {
        var result = Validator.Validate(ValidCommand() with { UserId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "user_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_current_password()
    {
        var result = Validator.Validate(ValidCommand() with { CurrentPassword = "" });

        result.Errors.Should().Contain(e => e.ErrorCode == "current_password_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_new_password()
    {
        var result = Validator.Validate(ValidCommand() with { NewPassword = "" });

        result.Errors.Should().Contain(e => e.ErrorCode == "new_password_required");
    }
}
