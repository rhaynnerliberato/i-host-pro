using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

public sealed class GetFrontDeskContactByCondominiumQueryHandler
    : IQueryHandler<GetFrontDeskContactByCondominiumQuery, FrontDeskContactResult>
{
    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);
    private static readonly Error FrontDeskContactNotFoundError = new(
        PropertyManagementErrorCodes.FrontDeskContactNotFound, PropertyManagementErrorCodes.FrontDeskContactNotFound);

    private readonly ICondominiumReader _condominiumReader;
    private readonly IFrontDeskContactRepository _repository;

    public GetFrontDeskContactByCondominiumQueryHandler(ICondominiumReader condominiumReader, IFrontDeskContactRepository repository)
    {
        _condominiumReader = condominiumReader;
        _repository = repository;
    }

    public async ValueTask<Result<FrontDeskContactResult>> Handle(
        GetFrontDeskContactByCondominiumQuery query, CancellationToken cancellationToken)
    {
        var condominium = await _condominiumReader.GetByIdAsync(query.CondominiumId, cancellationToken);
        if (condominium is null)
            return Result.Failure<FrontDeskContactResult>(CondominiumNotFoundError);

        var contact = await _repository.GetByCondominiumIdAsync(query.CondominiumId, cancellationToken);
        if (contact is null)
            return Result.Failure<FrontDeskContactResult>(FrontDeskContactNotFoundError);

        return Result.Success(new FrontDeskContactResult(
            contact.Id, contact.CondominiumId, contact.DisplayName, contact.PhoneNumber,
            contact.IsActive, contact.CreatedAtUtc, contact.UpdatedAtUtc));
    }
}
