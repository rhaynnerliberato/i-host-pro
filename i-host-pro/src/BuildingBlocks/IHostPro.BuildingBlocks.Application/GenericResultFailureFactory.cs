using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Builds a failure <see cref="Result"/>/<see cref="Result{TValue}"/> for a
/// pipeline response type known only at runtime. Used by pipeline behaviors that
/// need to short-circuit a request (e.g. <c>TenantBootstrapBehavior</c> rejecting
/// an unresolved tenant) without depending on the concrete TResponse of every
/// possible Command/Query.
/// </summary>
public static class GenericResultFailureFactory
{
    public static TResponse Create<TResponse>(Error error)
    {
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

        throw new InvalidOperationException(
            $"Cannot build a generic failure result for response type '{responseType.FullName}'.");
    }
}
