using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// The effective value following PROPERTY → TENANT → GLOBAL precedence —
/// dispatches to <c>IEarlyCheckInPolicyReader</c>/<c>ILateCheckoutPolicyReader</c>
/// (Checkpoint 3) by <see cref="PolicyCode"/>; those readers manage their own
/// short-lived transaction, so this query deliberately gets no
/// <c>TenantTransactionBehavior</c> wrapping (registering one would nest a
/// second transaction on the same <c>ConfigurationDbContext</c> instance and
/// throw).
/// </summary>
public sealed record GetEffectivePolicyQuery(Guid TenantId, string PolicyCode, Guid? PropertyId)
    : IQuery<EffectivePolicyResult>;
