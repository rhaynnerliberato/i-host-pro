using FluentAssertions;
using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.BuildingBlocks.Tests.Unit;

public class ValidationBehaviorTests
{
    private sealed record TestCommand(string Value) : ICommand;

    private sealed class AlwaysValidValidator : AbstractValidator<TestCommand>;

    private sealed class RequiredValueValidator : AbstractValidator<TestCommand>
    {
        public RequiredValueValidator() =>
            RuleFor(x => x.Value).NotEmpty().WithErrorCode("value_required").WithMessage("value_required");
    }

    private sealed class MaximumLengthValidator : AbstractValidator<TestCommand>
    {
        public MaximumLengthValidator() =>
            RuleFor(x => x.Value).MaximumLength(3).WithErrorCode("value_too_long").WithMessage("value_too_long");
    }

    private static ValueTask<Result> InvokeNext(TestCommand message, CancellationToken cancellationToken) =>
        new(Result.Success());

    [Fact]
    public async Task Handle_passes_through_to_next_when_there_are_no_validators()
    {
        var behavior = new ValidationBehavior<TestCommand, Result>([]);

        var result = await behavior.Handle(new TestCommand("x"), InvokeNext, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_passes_through_to_next_when_every_validator_succeeds()
    {
        var behavior = new ValidationBehavior<TestCommand, Result>([new AlwaysValidValidator()]);

        var result = await behavior.Handle(new TestCommand("x"), InvokeNext, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_preserves_the_validators_ErrorCode_as_the_results_error_code()
    {
        var behavior = new ValidationBehavior<TestCommand, Result>([new RequiredValueValidator()]);

        var result = await behavior.Handle(new TestCommand(string.Empty), InvokeNext, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("value_required");
    }

    [Fact]
    public async Task Handle_never_falls_back_to_a_hardcoded_ValidationFailed_code()
    {
        // The exact regression this fix addresses: Error.Code used to be
        // unconditionally "Validation.Failed", discarding whatever
        // ErrorCode each FluentValidation rule had set.
        var behavior = new ValidationBehavior<TestCommand, Result>([new RequiredValueValidator()]);

        var result = await behavior.Handle(new TestCommand(string.Empty), InvokeNext, CancellationToken.None);

        result.Error.Code.Should().NotBe("Validation.Failed");
    }

    [Fact]
    public async Task Handle_joins_every_failing_validators_ErrorCode_when_multiple_validators_fail()
    {
        var behavior = new ValidationBehavior<TestCommand, Result>(
            [new RequiredValueValidator(), new MaximumLengthValidator()]);

        var result = await behavior.Handle(new TestCommand("this is definitely too long"), InvokeNext, CancellationToken.None);

        result.Error.Code.Should().Contain("value_too_long");
    }

    [Fact]
    public async Task Handle_still_produces_a_Result_Failure_for_a_generic_ICommand_of_T_response()
    {
        // "Mantém compatibilidade com os resultados existentes": the
        // generic Result<TValue> branch (used by ICommand<TResponse>) must
        // keep working exactly as before, only the Code source changed.
        var behavior = new ValidationBehavior<TestCommand, Result<int>>([new RequiredValueValidator()]);

        var result = await behavior.Handle(
            new TestCommand(string.Empty),
            (_, _) => new ValueTask<Result<int>>(Result.Success(42)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("value_required");
    }

    [Fact]
    public async Task Handle_never_includes_the_rejected_value_in_the_error_code_or_message()
    {
        const string rejectedValue = "this-value-must-never-leak-into-the-error";
        var behavior = new ValidationBehavior<TestCommand, Result>([new MaximumLengthValidator()]);

        var result = await behavior.Handle(new TestCommand(rejectedValue), InvokeNext, CancellationToken.None);

        result.Error.Code.Should().NotContain(rejectedValue);
        result.Error.Message.Should().NotContain(rejectedValue);
    }
}
