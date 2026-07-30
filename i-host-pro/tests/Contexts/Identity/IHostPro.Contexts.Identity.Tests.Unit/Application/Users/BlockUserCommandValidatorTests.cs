using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Users;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

public class BlockUserCommandValidatorTests
{
    private static readonly BlockUserCommandValidator Validator = new();

    private static BlockUserCommand ValidCommand() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

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
}
