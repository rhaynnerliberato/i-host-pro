using System.Text.Json;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <inheritdoc cref="IEarlyCheckInPolicyReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IEarlyCheckInPolicyReader"/> — lives in
/// <c>Configuration.Infrastructure</c>, the one layer allowed to touch
/// <c>ConfigurationDbContext</c> directly (via <see cref="IPolicyValueResolver"/>).
/// Deliberately <c>internal</c> (mirrors <c>ConfigurationRequestDispatcher</c>):
/// no assembly outside this one can reference the concrete type at all, only
/// consume it through the public <see cref="IEarlyCheckInPolicyReader"/>
/// interface via dependency injection — a compiler-enforced guarantee, not
/// just a documented convention. Every exception other than a
/// caller-initiated cancellation is normalized into
/// <see cref="PolicyEngineUnavailableException"/> (Fase 5, Incremento 1
/// official decision 4: "unexpected error" is explicitly one of the named
/// unavailability causes) — a consumer never sees a raw database or
/// deserialization exception type leak across this contract boundary.
/// </remarks>
internal sealed class EarlyCheckInPolicyReader : IEarlyCheckInPolicyReader
{
    private const string PolicyCode = "EARLY_CHECKIN";

    private readonly IPolicyValueResolver _resolver;

    public EarlyCheckInPolicyReader(IPolicyValueResolver resolver) => _resolver = resolver;

    public async Task<PolicyReadResult<EarlyCheckInPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await _resolver.ResolveAsync(tenantId, PolicyCode, propertyId, cancellationToken);

            if (!resolution.Found)
                return PolicyReadResult<EarlyCheckInPolicy>.NotConfigured();

            var value = JsonSerializer.Deserialize<EarlyCheckInPolicy>(resolution.Value!, PolicyJsonOptions.Instance)
                ?? throw new PolicyEngineUnavailableException($"Stored value for policy '{PolicyCode}' deserialized to null.");

            return PolicyReadResult<EarlyCheckInPolicy>.Resolved(
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
