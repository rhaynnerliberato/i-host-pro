using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>Activates or deactivates an existing Template — fails with <see cref="Errors.TemplateErrorCodes.TemplateNotFound"/> when no Template exists for <paramref name="Key"/>.</summary>
public sealed record SetTemplateActiveCommand(string Key, bool IsActive) : ICommand<TemplateResult>;
