namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="ITemplateReader"/> returns to any
/// other Bounded Context — never a full Template projection (no
/// <c>Id</c>/<c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c>, which a consumer
/// resolving a template to send a message has no need for).
/// </summary>
public sealed record ActiveTemplate(string Key, string Content);
