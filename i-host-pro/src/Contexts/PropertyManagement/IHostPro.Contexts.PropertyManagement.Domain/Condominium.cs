using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// A physical condominium a tenant operates — always has an address
/// (Checkpoint 0 plan, item 4). Portarias, forms, rules and other §3 of
/// "Architecture Principles.md"'s responsibilities are explicitly deferred to
/// a later increment of this same Bounded Context (Checkpoint 0 plan, item
/// 2) — this increment only carries the minimal field set needed for a
/// Property to optionally reference one. No lifecycle of its own this
/// increment (Checkpoint 0 plan, item 4).
/// </summary>
public sealed class Condominium : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public Address Address { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Condominium()
    {
        // EF Core materialization.
    }

    private Condominium(Guid id, Guid tenantId, string name, Address address, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Address = address;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Condominium Create(Guid id, Guid tenantId, string name, Address address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));

        ArgumentNullException.ThrowIfNull(address);

        return new Condominium(id, tenantId, name.Trim(), address, now);
    }

    /// <summary>
    /// Renames this condominium (Checkpoint 2 plan, item 6). The caller
    /// (<c>UpdateCondominiumCommandHandler</c>) is responsible for comparing
    /// the trimmed value against <see cref="Name"/> first and only calling
    /// this when it actually differs — this method itself does not skip a
    /// no-op write, matching <c>User.ChangeFullName</c>'s division of
    /// responsibility.
    /// </summary>
    public void Rename(string newName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Name cannot be empty.", nameof(newName));

        Name = newName.Trim();
        Touch(now);
    }

    /// <summary>
    /// Replaces this condominium's address wholesale (Checkpoint 2 plan, item
    /// 6) — never a partial/field-level update of the Value Object. The
    /// caller is responsible for the no-op comparison, exactly like
    /// <see cref="Rename"/>.
    /// </summary>
    public void ChangeAddress(Address newAddress, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newAddress);

        Address = newAddress;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
