using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;

namespace IHostPro.Contexts.Configuration.Application.Policies;

public sealed class GetPolicyHistoryQueryHandler : IQueryHandler<GetPolicyHistoryQuery, IReadOnlyList<PolicyValueDetailResult>>
{
    private static readonly Error PolicyNotFoundError = new(PolicyErrorCodes.PolicyNotFound, PolicyErrorCodes.PolicyNotFound);

    private readonly IPolicyDefinitionReader _definitionReader;
    private readonly IPolicyValueReader _valueReader;

    public GetPolicyHistoryQueryHandler(IPolicyDefinitionReader definitionReader, IPolicyValueReader valueReader)
    {
        _definitionReader = definitionReader;
        _valueReader = valueReader;
    }

    public async ValueTask<Result<IReadOnlyList<PolicyValueDetailResult>>> Handle(GetPolicyHistoryQuery query, CancellationToken cancellationToken)
    {
        if (!await _definitionReader.ExistsAsync(query.PolicyCode, cancellationToken))
            return Result.Failure<IReadOnlyList<PolicyValueDetailResult>>(PolicyNotFoundError);

        if (!PolicyScopeParser.TryParse(query.ScopeType, query.PropertyId, out var scope, out var scopeError))
            return Result.Failure<IReadOnlyList<PolicyValueDetailResult>>(scopeError!);

        var history = await _valueReader.GetHistoryAsync(query.TenantId, query.PolicyCode, scope.Type, scope.ReferenceId, cancellationToken);

        return Result.Success(history);
    }
}
