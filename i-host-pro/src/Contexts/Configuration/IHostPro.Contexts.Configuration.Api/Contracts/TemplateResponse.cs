namespace IHostPro.Contexts.Configuration.Api.Contracts;

public sealed record TemplateResponse(
    Guid Id, string Key, string Content, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);
