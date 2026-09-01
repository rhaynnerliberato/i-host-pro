using System.Text.Json;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <inheritdoc cref="IAiAgentBehaviorPolicyReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IAiAgentBehaviorPolicyReader"/> (Fase 11, Checkpoint 7) —
/// mirrors <see cref="EarlyCheckInPolicyReader"/> exactly. Deliberately
/// <c>internal</c>: no assembly outside this one can reference the concrete
/// type, only consume it through the public interface via dependency
/// injection.
/// </remarks>
internal sealed class AiAgentBehaviorPolicyReader : IAiAgentBehaviorPolicyReader
{
    private const string PolicyCode = "AI_AGENT_BEHAVIOR";

    private readonly IPolicyValueResolver _resolver;

    public AiAgentBehaviorPolicyReader(IPolicyValueResolver resolver) => _resolver = resolver;

    public async Task<PolicyReadResult<AiAgentBehaviorPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await _resolver.ResolveAsync(tenantId, PolicyCode, propertyId, cancellationToken);

            if (!resolution.Found)
                return PolicyReadResult<AiAgentBehaviorPolicy>.NotConfigured();

            var value = JsonSerializer.Deserialize<AiAgentBehaviorPolicy>(resolution.Value!, PolicyJsonOptions.Instance)
                ?? throw new PolicyEngineUnavailableException($"Stored value for policy '{PolicyCode}' deserialized to null.");

            return PolicyReadResult<AiAgentBehaviorPolicy>.Resolved(
                value, PolicyResolvedScopeMapper.ToContractScope(resolution.ScopeKind!.Value), resolution.Version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PolicyEngineUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PolicyEngineUnavailableException($"The policy engine could not resolve '{PolicyCode}'.", ex);
        }
    }
}
