using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>Updates an existing Template's content — fails with <see cref="Errors.TemplateErrorCodes.TemplateNotFound"/> when no Template exists for <paramref name="Key"/>.</summary>
public sealed record UpdateTemplateContentCommand(string Key, string Content) : ICommand<TemplateResult>;
