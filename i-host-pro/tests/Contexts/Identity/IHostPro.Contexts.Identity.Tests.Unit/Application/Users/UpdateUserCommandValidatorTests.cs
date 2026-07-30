using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class UpdateUserCommandValidatorTests
{
    private static readonly UpdateUserCommandValidator Validator = new();

    private static UpdateUserCommand ValidCommand() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "New Name", "new@ihostpro.com");

    [Fact]
    public void Validate_succeeds_for_a_well_formed_command_with_both_fields()
    {
        Validator.Validate(ValidCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_succeeds_when_only_full_name_is_supplied()
    {
        Validator.Validate(ValidCommand() with { Email = null }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_succeeds_when_only_email_is_supplied()
    {
        Validator.Validate(ValidCommand() with { FullName = null }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_an_empty_tenant_id()
    {
        var result = Validator.Validate(ValidCommand() with { TenantId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_actor_id()
    {
        var result = Validator.Validate(ValidCommand() with { ActorId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "actor_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_target_user_id()
    {
        var result = Validator.Validate(ValidCommand() with { TargetUserId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "target_user_id_required");
    }

    [Fact]
    public void Validate_fails_for_a_whitespace_only_full_name()
    {
        var result = Validator.Validate(ValidCommand() with { FullName = "   " });

        result.Errors.Should().Contain(e => e.ErrorCode == "full_name_invalid");
    }

    [Fact]
    public void Validate_does_not_reject_a_null_full_name_the_same_way_as_a_blank_one()
    {
        // null means "not supplied" (Section 3) — only an explicitly supplied
        // blank string is invalid input; this is deliberately NOT the same
        // rejection as the whitespace-only case above.
        var result = Validator.Validate(ValidCommand() with { FullName = null });

        result.Errors.Should().NotContain(e => e.ErrorCode == "full_name_invalid");
    }
}
