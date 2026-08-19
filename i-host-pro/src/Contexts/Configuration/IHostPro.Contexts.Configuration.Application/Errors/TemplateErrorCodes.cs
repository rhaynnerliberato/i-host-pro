namespace IHostPro.Contexts.Configuration.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Template commands/queries (Fase 9, Checkpoint 1) — mirrors
/// <c>PolicyErrorCodes</c>'s own convention exactly.
/// </summary>
public static class TemplateErrorCodes
{
    /// <summary>No Template exists for the tenant/key combination.</summary>
    public const string TemplateNotFound = "Configuration.TemplateNotFound";

    /// <summary>A Template already exists for this tenant/key — exactly one row per (TenantId, Key), never a silent overwrite.</summary>
    public const string TemplateKeyAlreadyExists = "Configuration.TemplateKeyAlreadyExists";
}
