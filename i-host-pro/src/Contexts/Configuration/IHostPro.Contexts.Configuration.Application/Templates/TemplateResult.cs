namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>A read-only projection of a single <c>Template</c> row, used by this context's own administrative API.</summary>
public sealed record TemplateResult(
    Guid Id, string Key, string Content, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);
