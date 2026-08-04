using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

public sealed class GetCondominiumDetailQueryHandler : IQueryHandler<GetCondominiumDetailQuery, CondominiumResult>
{
    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);

    private readonly ICondominiumReader _reader;

    public GetCondominiumDetailQueryHandler(ICondominiumReader reader) => _reader = reader;

    public async ValueTask<Result<CondominiumResult>> Handle(GetCondominiumDetailQuery query, CancellationToken cancellationToken)
    {
        var result = await _reader.GetByIdAsync(query.CondominiumId, cancellationToken);

        return result is null
            ? Result.Failure<CondominiumResult>(CondominiumNotFoundError)
            : Result.Success(result);
    }
}
