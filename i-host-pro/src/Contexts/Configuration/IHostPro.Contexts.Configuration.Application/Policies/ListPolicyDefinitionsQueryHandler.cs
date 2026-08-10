using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

public sealed class ListPolicyDefinitionsQueryHandler
    : IQueryHandler<ListPolicyDefinitionsQuery, IReadOnlyList<PolicyDefinitionResult>>
{
    private readonly IPolicyDefinitionReader _reader;

    public ListPolicyDefinitionsQueryHandler(IPolicyDefinitionReader reader) => _reader = reader;

    public async ValueTask<Result<IReadOnlyList<PolicyDefinitionResult>>> Handle(
        ListPolicyDefinitionsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await _reader.ListAsync(cancellationToken));
}
