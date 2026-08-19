using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// A reusable, tenant-owned message template (Fase 9, Checkpoint 1 —
/// "Comunicação e Integrações do MVP"). Lives in Configuration &amp; Policy
/// per <c>Architecture Principles.md</c> §3 ("Configurações, Políticas,
/// Templates, Feature Flags") — never its own Bounded Context. Deliberately
/// minimal for this checkpoint (Fase 9 doc, "Template MVP"): create, read,
/// update, activate/deactivate, resolve the active template for a given
/// <see cref="Key"/> — no version history, no rollback, no multi-channel
/// variants, no visual editor. Exactly one row may exist per
/// (<see cref="TenantId"/>, <see cref="Key"/>) — enforced by a unique index —
/// so "the active template for this key" is never ambiguous.
/// <see cref="Content"/> may contain <c>{{Variable}}</c> placeholders,
/// interpolated only against an explicit, caller-known allow-list (see
/// <c>Communication.Application</c>'s renderer) — never reflection,
/// expressions, or a scripting/templating library.
/// </summary>
public sealed class Template : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = null!;
    public string Content { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private Template()
    {
        // EF Core materialization.
    }

    private Template(Guid id, Guid tenantId, string key, string content, DateTimeOffset createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        Key = key;
        Content = content;
        IsActive = true;
        CreatedAtUtc = createdAtUtc;
    }

    public static Template Create(Guid id, Guid tenantId, string key, string content, DateTimeOffset createdAtUtc)
    {
        ValidateKey(key);
        ValidateContent(content);

        return new Template(id, tenantId, key, content, createdAtUtc);
    }

    public void UpdateContent(string content, DateTimeOffset updatedAtUtc)
    {
        ValidateContent(content);

        Content = content;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Activate(DateTimeOffset updatedAtUtc)
    {
        IsActive = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Deactivate(DateTimeOffset updatedAtUtc)
    {
        IsActive = false;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Template key cannot be empty.", nameof(key));
        if (key.Length > 100)
            throw new ArgumentException("Template key cannot exceed 100 characters.", nameof(key));
    }

    private static void ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Template content cannot be empty.", nameof(content));
    }
}
