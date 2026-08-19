using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>Creates a new Template — fails with <see cref="Errors.TemplateErrorCodes.TemplateKeyAlreadyExists"/> if <paramref name="Key"/> already exists for the tenant.</summary>
public sealed record CreateTemplateCommand(Guid TenantId, string Key, string Content) : ICommand<TemplateResult>;
