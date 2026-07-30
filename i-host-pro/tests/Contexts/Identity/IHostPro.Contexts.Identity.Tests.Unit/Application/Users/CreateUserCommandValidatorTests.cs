using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class CreateUserCommandValidatorTests
{
    private static readonly CreateUserCommandValidator Validator = new();

    private static CreateUserCommand Valid() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Test User", "test@ihostpro.com", "Correct-Horse-Battery-Staple-42!", "ADMIN");

    [Fact]
    public void Validate_succeeds_for_a_well_formed_command()
    {
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_an_empty_tenant_id()
    {
        var result = Validator.Validate(Valid() with { TenantId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_actor_id()
    {
        var result = Validator.Validate(Valid() with { ActorId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "actor_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_full_name()
    {
        var result = Validator.Validate(Valid() with { FullName = string.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "full_name_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_email()
    {
        var result = Validator.Validate(Valid() with { Email = string.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "email_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_initial_password()
    {
        var result = Validator.Validate(Valid() with { InitialPassword = string.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "initial_password_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_role_code()
    {
        var result = Validator.Validate(Valid() with { RoleCode = string.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "role_code_required");
    }

    [Fact]
    public void Validate_accumulates_every_failure_instead_of_stopping_at_the_first()
    {
        var command = new CreateUserCommand(Guid.Empty, Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

        var result = Validator.Validate(command);

        result.Errors.Select(e => e.ErrorCode).Should().Contain([
            "tenant_id_required", "actor_id_required", "full_name_required",
            "email_required", "initial_password_required", "role_code_required",
        ]);
    }
}
