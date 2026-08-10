using IHostPro.Contexts.Configuration.Infrastructure.Resolution;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Resolution;

internal sealed class FakePolicyValueResolver : IPolicyValueResolver
{
    private readonly PolicyValueResolution? _result;
    private readonly Exception? _exception;

    public int CallCount { get; private set; }

    private FakePolicyValueResolver(PolicyValueResolution? result, Exception? exception)
    {
        _result = result;
        _exception = exception;
    }

    public static FakePolicyValueResolver Returning(PolicyValueResolution result) => new(result, null);

    public static FakePolicyValueResolver Throwing(Exception exception) => new(null, exception);

    public Task<PolicyValueResolution> ResolveAsync(Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken)
    {
        CallCount++;
        return _exception is not null ? Task.FromException<PolicyValueResolution>(_exception) : Task.FromResult(_result!);
    }
}
