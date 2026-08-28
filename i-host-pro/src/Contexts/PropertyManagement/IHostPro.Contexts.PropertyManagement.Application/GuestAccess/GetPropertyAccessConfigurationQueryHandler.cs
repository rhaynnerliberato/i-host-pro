using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

public sealed class GetPropertyAccessConfigurationQueryHandler
    : IQueryHandler<GetPropertyAccessConfigurationQuery, PropertyAccessConfigurationResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error PropertyAccessConfigurationNotFoundError = new(
        PropertyManagementErrorCodes.PropertyAccessConfigurationNotFound, PropertyManagementErrorCodes.PropertyAccessConfigurationNotFound);

    private readonly IPropertyReader _propertyReader;
    private readonly IPropertyAccessConfigurationRepository _repository;

    public GetPropertyAccessConfigurationQueryHandler(IPropertyReader propertyReader, IPropertyAccessConfigurationRepository repository)
    {
        _propertyReader = propertyReader;
        _repository = repository;
    }

    public async ValueTask<Result<PropertyAccessConfigurationResult>> Handle(
        GetPropertyAccessConfigurationQuery query, CancellationToken cancellationToken)
    {
        var property = await _propertyReader.GetByIdAsync(query.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PropertyAccessConfigurationResult>(PropertyNotFoundError);

        var configuration = await _repository.GetByPropertyIdAsync(query.PropertyId, cancellationToken);
        if (configuration is null)
            return Result.Failure<PropertyAccessConfigurationResult>(PropertyAccessConfigurationNotFoundError);

        return Result.Success(new PropertyAccessConfigurationResult(
            configuration.Id, configuration.PropertyId, configuration.AccessCredentialSecretReference,
            configuration.AccessInstructions, configuration.IsActive, configuration.CreatedAtUtc, configuration.UpdatedAtUtc));
    }
}
