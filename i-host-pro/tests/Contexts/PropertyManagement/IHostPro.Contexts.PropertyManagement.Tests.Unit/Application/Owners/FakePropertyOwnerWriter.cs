using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyOwnerWriter : IPropertyOwnerWriter
{
    public List<PropertyOwnerLink> LinkedLinks { get; } = [];
    public List<PropertyOwnerLink> UnlinkedLinks { get; } = [];

    public void Link(PropertyOwnerLink link) => LinkedLinks.Add(link);

    public void Unlink(PropertyOwnerLink link) => UnlinkedLinks.Add(link);
}
