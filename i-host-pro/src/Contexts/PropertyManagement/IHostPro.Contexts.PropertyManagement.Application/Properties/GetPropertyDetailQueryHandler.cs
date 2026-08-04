using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

public sealed class GetPropertyDetailQueryHandler : IQueryHandler<GetPropertyDetailQuery, PropertyResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);

    private readonly IPropertyReader _reader;

    public GetPropertyDetailQueryHandler(IPropertyReader reader) => _reader = reader;

    public async ValueTask<Result<PropertyResult>> Handle(GetPropertyDetailQuery query, CancellationToken cancellationToken)
    {
        var result = await _reader.GetByIdAsync(query.PropertyId, cancellationToken);

        return result is null
            ? Result.Failure<PropertyResult>(PropertyNotFoundError)
            : Result.Success(result);
    }
}
