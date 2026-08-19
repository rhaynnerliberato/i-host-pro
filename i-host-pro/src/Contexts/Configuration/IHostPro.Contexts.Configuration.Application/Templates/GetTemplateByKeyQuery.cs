using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>Reads a single Template by key. Fails with <see cref="Errors.TemplateErrorCodes.TemplateNotFound"/> when none exists for the tenant.</summary>
public sealed record GetTemplateByKeyQuery(string Key) : IQuery<TemplateResult>;
