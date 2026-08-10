using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;

namespace IHostPro.Contexts.Configuration.Application.Policies;

public sealed class GetPolicyValueByScopeQueryHandler : IQueryHandler<GetPolicyValueByScopeQuery, PolicyValueDetailResult>
{
    private static readonly Error PolicyNotFoundError = new(PolicyErrorCodes.PolicyNotFound, PolicyErrorCodes.PolicyNotFound);
    private static readonly Error PolicyNotConfiguredError = new(PolicyErrorCodes.PolicyNotConfigured, PolicyErrorCodes.PolicyNotConfigured);

    private readonly IPolicyDefinitionReader _definitionReader;
    private readonly IPolicyValueReader _valueReader;

    public GetPolicyValueByScopeQueryHandler(IPolicyDefinitionReader definitionReader, IPolicyValueReader valueReader)
    {
        _definitionReader = definitionReader;
        _valueReader = valueReader;
    }

    public async ValueTask<Result<PolicyValueDetailResult>> Handle(GetPolicyValueByScopeQuery query, CancellationToken cancellationToken)
    {
        if (!await _definitionReader.ExistsAsync(query.PolicyCode, cancellationToken))
            return Result.Failure<PolicyValueDetailResult>(PolicyNotFoundError);

        if (!PolicyScopeParser.TryParse(query.ScopeType, query.PropertyId, out var scope, out var scopeError))
            return Result.Failure<PolicyValueDetailResult>(scopeError!);

        var value = await _valueReader.GetCurrentAsync(query.TenantId, query.PolicyCode, scope.Type, scope.ReferenceId, cancellationToken);

        return value is null
            ? Result.Failure<PolicyValueDetailResult>(PolicyNotConfiguredError)
            : Result.Success(value);
    }
}
