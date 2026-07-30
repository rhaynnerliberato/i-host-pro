using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Sessions;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Sessions;

public class RevokeOwnSessionCommandValidatorTests
{
    private static readonly RevokeOwnSessionCommandValidator Validator = new();

    private static RevokeOwnSessionCommand Valid() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

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
    public void Validate_fails_for_an_empty_user_id()
    {
        var result = Validator.Validate(Valid() with { UserId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "user_id_required");
    }

    [Fact]
    public void Validate_fails_for_an_empty_session_id()
    {
        var result = Validator.Validate(Valid() with { SessionId = Guid.Empty });

        result.Errors.Should().Contain(e => e.ErrorCode == "session_id_required");
    }

    [Fact]
    public void Validate_accumulates_every_failure_instead_of_stopping_at_the_first()
    {
        var command = new RevokeOwnSessionCommand(Guid.Empty, Guid.Empty, Guid.Empty);

        var result = Validator.Validate(command);

        result.Errors.Select(e => e.ErrorCode).Should().Contain(
            ["tenant_id_required", "user_id_required", "session_id_required"]);
    }
}
