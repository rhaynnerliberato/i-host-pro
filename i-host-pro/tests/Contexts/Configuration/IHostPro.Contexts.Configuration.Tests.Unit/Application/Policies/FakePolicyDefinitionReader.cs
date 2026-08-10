using IHostPro.Contexts.Configuration.Application.Policies;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePolicyDefinitionReader : IPolicyDefinitionReader
{
    private readonly HashSet<string> _existingCodes;
    private readonly IReadOnlyList<PolicyDefinitionResult> _definitions;

    private FakePolicyDefinitionReader(HashSet<string> existingCodes, IReadOnlyList<PolicyDefinitionResult> definitions)
    {
        _existingCodes = existingCodes;
        _definitions = definitions;
    }

    public static FakePolicyDefinitionReader WithCodes(params string[] codes) =>
        new(codes.ToHashSet(StringComparer.Ordinal), []);

    public static FakePolicyDefinitionReader WithDefinitions(params PolicyDefinitionResult[] definitions) =>
        new(definitions.Select(d => d.Code).ToHashSet(StringComparer.Ordinal), definitions);

    public Task<IReadOnlyList<PolicyDefinitionResult>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_definitions);

    public Task<bool> ExistsAsync(string policyCode, CancellationToken cancellationToken) =>
        Task.FromResult(_existingCodes.Contains(policyCode));
}
