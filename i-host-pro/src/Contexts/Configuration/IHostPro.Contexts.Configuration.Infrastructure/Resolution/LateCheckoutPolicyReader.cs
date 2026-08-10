using System.Text.Json;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <inheritdoc cref="ILateCheckoutPolicyReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="ILateCheckoutPolicyReader"/> — lives in
/// <c>Configuration.Infrastructure</c>, the one layer allowed to touch
/// <c>ConfigurationDbContext</c> directly (via <see cref="IPolicyValueResolver"/>).
/// Deliberately <c>internal</c> (mirrors <c>ConfigurationRequestDispatcher</c>):
/// no assembly outside this one can reference the concrete type at all, only
/// consume it through the public <see cref="ILateCheckoutPolicyReader"/>
/// interface via dependency injection — a compiler-enforced guarantee, not
/// just a documented convention. Every exception other than a
/// caller-initiated cancellation is normalized into
/// <see cref="PolicyEngineUnavailableException"/> (Fase 5, Incremento 1
/// official decision 4: "unexpected error" is explicitly one of the named
/// unavailability causes) — a consumer never sees a raw database or
/// deserialization exception type leak across this contract boundary.
/// </remarks>
internal sealed class LateCheckoutPolicyReader : ILateCheckoutPolicyReader
{
    private const string PolicyCode = "LATE_CHECKOUT";

    private readonly IPolicyValueResolver _resolver;

    public LateCheckoutPolicyReader(IPolicyValueResolver resolver) => _resolver = resolver;

    public async Task<PolicyReadResult<LateCheckoutPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await _resolver.ResolveAsync(tenantId, PolicyCode, propertyId, cancellationToken);

            if (!resolution.Found)
                return PolicyReadResult<LateCheckoutPolicy>.NotConfigured();

            var value = JsonSerializer.Deserialize<LateCheckoutPolicy>(resolution.Value!, PolicyJsonOptions.Instance)
                ?? throw new PolicyEngineUnavailableException($"Stored value for policy '{PolicyCode}' deserialized to null.");

            return PolicyReadResult<LateCheckoutPolicy>.Resolved(
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
