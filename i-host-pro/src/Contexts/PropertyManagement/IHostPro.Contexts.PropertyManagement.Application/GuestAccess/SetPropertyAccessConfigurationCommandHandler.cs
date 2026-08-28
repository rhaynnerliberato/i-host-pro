using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

/// <summary>
/// Upserts the single guest-access configuration for a Property (Fase 10,
/// Checkpoint 6.2). Runs inside the transaction
/// <see cref="SetPropertyAccessConfigurationTenantAwareBehavior"/> opens for
/// this command — never calls <c>SaveChangesAsync</c> itself. Deliberately
/// publishes NO Integration Event — resolution is synchronous, via
/// <c>IPropertyGuestAccessReader</c>, so Communication never needs a
/// <c>PropertyAccessConfigurationCreated</c>/<c>Updated</c> event (mirrors
/// <c>SetFrontDeskContactCommandHandler</c>'s own reasoning exactly).
/// </summary>
public sealed class SetPropertyAccessConfigurationCommandHandler
    : ICommandHandler<SetPropertyAccessConfigurationCommand, PropertyAccessConfigurationResult>
{
    private const string CreatedActionCode = "property_access_configuration_created";
    private const string UpdatedActionCode = "property_access_configuration_updated";

    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);

    private readonly IPropertyReader _propertyReader;
    private readonly IPropertyAccessConfigurationRepository _repository;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public SetPropertyAccessConfigurationCommandHandler(
        IPropertyReader propertyReader,
        IPropertyAccessConfigurationRepository repository,
        IPropertyAuditWriter auditWriter,
        TimeProvider timeProvider)
    {
        _propertyReader = propertyReader;
        _repository = repository;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<PropertyAccessConfigurationResult>> Handle(
        SetPropertyAccessConfigurationCommand command, CancellationToken cancellationToken)
    {
        var property = await _propertyReader.GetByIdAsync(command.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PropertyAccessConfigurationResult>(PropertyNotFoundError);

        var now = _timeProvider.GetUtcNow();
        var normalizedCredentialReference = Normalize(command.AccessCredentialSecretReference);
        var normalizedInstructions = Normalize(command.AccessInstructions);

        var existing = await _repository.GetByPropertyIdAsync(command.PropertyId, cancellationToken);

        PropertyAccessConfiguration configuration;
        if (existing is null)
        {
            configuration = PropertyAccessConfiguration.Create(
                Guid.NewGuid(), command.TenantId, command.PropertyId,
                normalizedCredentialReference, normalizedInstructions, command.IsActive, now);
            _repository.Add(configuration);

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "PropertyAccessConfiguration", configuration.Id,
                CreatedActionCode, changedFields: ["access_credential_secret_reference", "access_instructions", "is_active"], now));

            return Result.Success(ToResult(configuration));
        }

        configuration = existing;
        var changedFields = new List<string>();

        if (!string.Equals(normalizedCredentialReference, configuration.AccessCredentialSecretReference, StringComparison.Ordinal))
            changedFields.Add("access_credential_secret_reference");
        if (!string.Equals(normalizedInstructions, configuration.AccessInstructions, StringComparison.Ordinal))
            changedFields.Add("access_instructions");
        if (command.IsActive != configuration.IsActive)
            changedFields.Add("is_active");

        if (changedFields.Count > 0)
        {
            configuration.UpdateConfiguration(normalizedCredentialReference, normalizedInstructions, command.IsActive, now);
            _repository.Update(configuration);

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "PropertyAccessConfiguration", configuration.Id,
                UpdatedActionCode, changedFields, now));
        }

        return Result.Success(ToResult(configuration));
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PropertyAccessConfigurationResult ToResult(PropertyAccessConfiguration configuration) => new(
        configuration.Id, configuration.PropertyId, configuration.AccessCredentialSecretReference,
        configuration.AccessInstructions, configuration.IsActive, configuration.CreatedAtUtc, configuration.UpdatedAtUtc);
}
