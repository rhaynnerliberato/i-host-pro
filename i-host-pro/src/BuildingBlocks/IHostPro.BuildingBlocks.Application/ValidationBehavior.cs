using FluentValidation;
using IHostPro.BuildingBlocks.Domain;
using Mediator;

namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Generic pipeline behavior that validates a Command/Query against its registered
/// FluentValidation validators before the handler executes. Contains no business
/// rule of any specific Bounded Context — only the generic mechanism.
/// </summary>
public sealed class ValidationBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    private readonly IEnumerable<IValidator<TMessage>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TMessage>> validators)
    {
        _validators = validators;
    }

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next(message, cancellationToken);

        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(message, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
            return await next(message, cancellationToken);

        // Error.Code is built exclusively from each failure's own ErrorCode
        // (never a hardcoded "Validation.Failed" and never derived from
        // ErrorMessage) — a validator that never called .WithErrorCode(...)
        // still contributes FluentValidation's own stable per-validator-type
        // default (e.g. "NotEmptyValidator"), never null/empty. Only
        // ErrorMessage is used for the human-readable summary; neither this
        // join nor the one below ever reads ValidationFailure.AttemptedValue
        // — the rejected value itself must never appear here, only whatever
        // static, value-free message the validator itself chose to set.
        var error = new Error(
            string.Join(",", failures.Select(f => f.ErrorCode)),
            string.Join("; ", failures.Select(f => f.ErrorMessage)));

        var responseType = typeof(TResponse);

        if (responseType == typeof(Result))
            return (TResponse)(object)Result.Failure(error);

        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var failureMethod = typeof(Result)
                .GetMethod(nameof(Result.Failure), 1, new[] { typeof(Error) })!
                .MakeGenericMethod(responseType.GetGenericArguments()[0]);

            return (TResponse)failureMethod.Invoke(null, new object[] { error })!;
        }

        throw new ValidationException(failures);
    }
}
