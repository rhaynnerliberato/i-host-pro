using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Dispatches to the correct Checkpoint 3 typed reader by
/// <see cref="GetEffectivePolicyQuery.PolicyCode"/> — an unknown code is
/// <c>policy_not_found</c>, the same outcome as every other endpoint in this
/// API. <see cref="PolicyEngineUnavailableException"/> is deliberately never
/// caught here: it is not one of the seven documented ProblemDetails
/// outcomes (Fase 5, Incremento 1 official decision 4) and must propagate as
/// a genuine failure of the operation, never silently downgraded.
/// </summary>
public sealed class GetEffectivePolicyQueryHandler : IQueryHandler<GetEffectivePolicyQuery, EffectivePolicyResult>
{
    private static readonly Error PolicyNotFoundError = new(PolicyErrorCodes.PolicyNotFound, PolicyErrorCodes.PolicyNotFound);

    private readonly IEarlyCheckInPolicyReader _earlyCheckInReader;
    private readonly ILateCheckoutPolicyReader _lateCheckoutReader;

    public GetEffectivePolicyQueryHandler(IEarlyCheckInPolicyReader earlyCheckInReader, ILateCheckoutPolicyReader lateCheckoutReader)
    {
        _earlyCheckInReader = earlyCheckInReader;
        _lateCheckoutReader = lateCheckoutReader;
    }

    public async ValueTask<Result<EffectivePolicyResult>> Handle(GetEffectivePolicyQuery query, CancellationToken cancellationToken)
    {
        switch (query.PolicyCode)
        {
            case "EARLY_CHECKIN":
            {
                var result = await _earlyCheckInReader.GetEffectiveAsync(query.TenantId, query.PropertyId, cancellationToken);
                return Result.Success(new EffectivePolicyResult(query.PolicyCode, result.Status, result.Value, result.ResolvedScope, result.Version));
            }
            case "LATE_CHECKOUT":
            {
                var result = await _lateCheckoutReader.GetEffectiveAsync(query.TenantId, query.PropertyId, cancellationToken);
                return Result.Success(new EffectivePolicyResult(query.PolicyCode, result.Status, result.Value, result.ResolvedScope, result.Version));
            }
            default:
                return Result.Failure<EffectivePolicyResult>(PolicyNotFoundError);
        }
    }
}
