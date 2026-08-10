namespace IHostPro.Contexts.Configuration.Api.Contracts;

public sealed record PolicyDefinitionResponse(
    string Code, string Name, string Description, string Category, string ValueType, int SchemaVersion, bool IsActive);
