namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>Read-only access to the seeded <c>PolicyDefinition</c> catalog — implemented in Infrastructure, the only layer allowed to touch <c>ConfigurationDbContext</c>.</summary>
public interface IPolicyDefinitionReader
{
    Task<IReadOnlyList<PolicyDefinitionResult>> ListAsync(CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string policyCode, CancellationToken cancellationToken);
}
