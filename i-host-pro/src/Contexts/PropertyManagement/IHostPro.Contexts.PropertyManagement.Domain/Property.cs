using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Domain;

/// <summary>
/// A rentable unit — the Property Management Bounded Context's central
/// aggregate (Checkpoint 0 plan, item 6). Every Property is born in
/// <see cref="PropertyStatus.Draft"/> (Checkpoint 0 plan, item 7); the full
/// lifecycle (Activate/Deactivate/Archive and their transition rules) is
/// implemented in Checkpoint 4 — this constructor only enforces the
/// structural invariants that must hold from the moment a Property exists:
/// <see cref="Code"/>/<see cref="Name"/> required, <see cref="Capacity"/>
/// positive, and the address-source rule (Checkpoint 0 plan, item 5): a
/// Property with no <see cref="CondominiumId"/> must carry its own
/// <see cref="Address"/> — mirrored at the database level by the
/// <c>ck_properties_effective_address_source</c> CHECK constraint, since
/// this in-process invariant alone cannot prevent a concurrent write from
/// violating it.
/// </summary>
public sealed class Property : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public PropertyCode Code { get; private set; } = null!;
    public string NormalizedCode { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public int Capacity { get; private set; }
    public Guid? CondominiumId { get; private set; }
    public Address? Address { get; private set; }
    public string? TimeZoneId { get; private set; }
    public PropertyStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Property()
    {
        // EF Core materialization.
    }

    private Property(
        Guid id, Guid tenantId, PropertyCode code, string name, int capacity,
        Guid? condominiumId, Address? address, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        Code = code;
        NormalizedCode = code.NormalizedValue;
        Name = name;
        Capacity = capacity;
        CondominiumId = condominiumId;
        Address = address;
        Status = PropertyStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static Property Create(
        Guid id, Guid tenantId, PropertyCode code, string name, int capacity,
        Guid? condominiumId, Address? address, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));

        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));

        if (condominiumId is null && address is null)
        {
            throw new ArgumentException(
                "A Property without a Condominium must have its own Address.", nameof(address));
        }

        return new Property(id, tenantId, code, name.Trim(), capacity, condominiumId, address, now);
    }

    /// <summary>
    /// Renames this property (Checkpoint 3 plan, item 4). The caller
    /// (<c>UpdatePropertyCommandHandler</c>) is responsible for the no-op
    /// comparison — mirrors <c>Condominium.Rename</c>.
    /// </summary>
    public void Rename(string newName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Name cannot be empty.", nameof(newName));

        Name = newName.Trim();
        Touch(now);
    }

    public void ChangeCode(PropertyCode newCode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newCode);

        Code = newCode;
        NormalizedCode = newCode.NormalizedValue;
        Touch(now);
    }

    public void ChangeCapacity(int newCapacity, DateTimeOffset now)
    {
        if (newCapacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero.", nameof(newCapacity));

        Capacity = newCapacity;
        Touch(now);
    }

    /// <summary>
    /// Reassigns or removes this property's condominium (Checkpoint 3 plan,
    /// item 4) — <c>null</c> removes the association. The caller is
    /// responsible for validating the target condominium exists (within the
    /// same tenant) BEFORE calling this, and for validating the resulting
    /// combined state still has an effective address source (Checkpoint 3
    /// plan, item 4/7) — this method itself enforces neither, since it
    /// cannot see the property's prospective final <see cref="Address"/>
    /// when both fields change in the same PATCH.
    /// </summary>
    public void ChangeCondominium(Guid? newCondominiumId, DateTimeOffset now)
    {
        CondominiumId = newCondominiumId;
        Touch(now);
    }

    /// <summary>
    /// Replaces or removes this property's own address wholesale (Checkpoint
    /// 3 plan, item 4) — <c>null</c> removes it; never a partial/field-level
    /// update. Same caller-responsibility split as <see cref="ChangeCondominium"/>.
    /// </summary>
    public void ChangeAddress(Address? newAddress, DateTimeOffset now)
    {
        Address = newAddress;
        Touch(now);
    }

    /// <summary>
    /// Sets or removes this property's IANA time zone identifier (Fase 11,
    /// Checkpoint 7 — <c>TimezoneOwner=Property</c>). <c>null</c> removes it;
    /// the caller (<c>UpdatePropertyCommandHandler</c>) is responsible for
    /// validating the supplied id is a genuine IANA identifier BEFORE calling
    /// this — this method only enforces that a non-null value is not blank.
    /// Deliberately never set by <see cref="Create"/>/never backfilled for
    /// existing properties (mandate item 31) — nullable until an
    /// administrator configures it explicitly for each property.
    /// </summary>
    public void ChangeTimeZone(string? newTimeZoneId, DateTimeOffset now)
    {
        if (newTimeZoneId is not null && string.IsNullOrWhiteSpace(newTimeZoneId))
            throw new ArgumentException("Time zone id cannot be blank.", nameof(newTimeZoneId));

        TimeZoneId = newTimeZoneId;
        Touch(now);
    }

    /// <summary>
    /// <see cref="PropertyStatus.Draft"/>/<see cref="PropertyStatus.Inactive"/>
    /// → <see cref="PropertyStatus.Active"/> (Checkpoint 4 plan, item 3). The
    /// caller (<c>ActivatePropertyCommandHandler</c>) is responsible for
    /// translating an invalid starting state into the precise stable error
    /// code (<c>PropertyAlreadyActive</c>/<c>PropertyAlreadyArchived</c>) —
    /// this guard is defense-in-depth, not the primary rejection path, so it
    /// throws generically rather than returning a <c>Result</c>. Also
    /// responsible for validating an effective address exists BEFORE calling
    /// this (Checkpoint 4 plan, item 5) — this method has no address
    /// awareness of its own.
    /// </summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status is not (PropertyStatus.Draft or PropertyStatus.Inactive))
            throw new InvalidOperationException($"Cannot activate a property in status '{Status}'.");

        Status = PropertyStatus.Active;
        Touch(now);
    }

    /// <summary>
    /// <see cref="PropertyStatus.Active"/> → <see cref="PropertyStatus.Inactive"/>
    /// (Checkpoint 4 plan, item 3). Same caller-responsibility split as
    /// <see cref="Activate"/>.
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        if (Status != PropertyStatus.Active)
            throw new InvalidOperationException($"Cannot deactivate a property in status '{Status}'.");

        Status = PropertyStatus.Inactive;
        Touch(now);
    }

    /// <summary>
    /// <see cref="PropertyStatus.Draft"/>/<see cref="PropertyStatus.Inactive"/>
    /// → <see cref="PropertyStatus.Archived"/> — terminal in this increment,
    /// no restoration exists (Checkpoint 4 plan, item 3). Same
    /// caller-responsibility split as <see cref="Activate"/>.
    /// </summary>
    public void Archive(DateTimeOffset now)
    {
        if (Status is not (PropertyStatus.Draft or PropertyStatus.Inactive))
            throw new InvalidOperationException($"Cannot archive a property in status '{Status}'.");

        Status = PropertyStatus.Archived;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
