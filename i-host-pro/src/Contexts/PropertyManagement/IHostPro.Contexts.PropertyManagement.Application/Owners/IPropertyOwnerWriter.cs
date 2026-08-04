using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Stages <see cref="PropertyOwnerLink"/> mutations within the current
/// tenant-aware transaction (Checkpoint 5 plan) — the write-side counterpart
/// to <see cref="IPropertyOwnerReader"/>. Never calls <c>SaveChangesAsync</c>
/// itself.
/// </summary>
public interface IPropertyOwnerWriter
{
    void Link(PropertyOwnerLink link);

    /// <summary>
    /// Stages removal of an already-tracked <see cref="PropertyOwnerLink"/>
    /// row — callers fetch it via <see cref="IPropertyOwnerReader.FindAsync"/>
    /// first, mirroring <c>IUserRoleWriter.Remove</c>.
    /// </summary>
    void Unlink(PropertyOwnerLink link);
}
