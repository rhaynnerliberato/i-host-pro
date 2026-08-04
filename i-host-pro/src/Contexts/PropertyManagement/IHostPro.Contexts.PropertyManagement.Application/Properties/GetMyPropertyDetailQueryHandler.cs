using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

public sealed class GetMyPropertyDetailQueryHandler : IQueryHandler<GetMyPropertyDetailQuery, PropertyResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);

    private readonly IPropertyReader _reader;

    public GetMyPropertyDetailQueryHandler(IPropertyReader reader) => _reader = reader;

    public async ValueTask<Result<PropertyResult>> Handle(GetMyPropertyDetailQuery query, CancellationToken cancellationToken)
    {
        var result = await _reader.GetMineDetailAsync(query.OwnerUserId, query.PropertyId, cancellationToken);

        return result is null
            ? Result.Failure<PropertyResult>(PropertyNotFoundError)
            : Result.Success(result);
    }
}
