using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

public sealed class ListPropertyOwnersQueryHandler : IQueryHandler<ListPropertyOwnersQuery, PagedResult<PropertyOwnerResult>>
{
    public const int DefaultPageSize = 20;

    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);

    private readonly IRepository<Property, Guid> _propertyRepository;
    private readonly IPropertyOwnerReader _ownerReader;

    public ListPropertyOwnersQueryHandler(IRepository<Property, Guid> propertyRepository, IPropertyOwnerReader ownerReader)
    {
        _propertyRepository = propertyRepository;
        _ownerReader = ownerReader;
    }

    public async ValueTask<Result<PagedResult<PropertyOwnerResult>>> Handle(
        ListPropertyOwnersQuery query, CancellationToken cancellationToken)
    {
        var property = await _propertyRepository.GetByIdAsync(query.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PagedResult<PropertyOwnerResult>>(PropertyNotFoundError);

        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _ownerReader.ListByPropertyAsync(query.PropertyId, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
