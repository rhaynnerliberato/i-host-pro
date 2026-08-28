using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.GuestAccess;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyAccessConfigurationRepository : IPropertyAccessConfigurationRepository
{
    private readonly PropertyAccessConfiguration? _existing;

    private FakePropertyAccessConfigurationRepository(PropertyAccessConfiguration? existing) => _existing = existing;

    public static FakePropertyAccessConfigurationRepository WithExisting(PropertyAccessConfiguration? existing) => new(existing);

    public List<PropertyAccessConfiguration> AddedConfigurations { get; } = [];
    public List<PropertyAccessConfiguration> UpdatedConfigurations { get; } = [];

    public Task<PropertyAccessConfiguration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_existing?.Id == id ? _existing : null);

    public Task<PropertyAccessConfiguration?> GetByPropertyIdAsync(Guid propertyId, CancellationToken cancellationToken) =>
        Task.FromResult(_existing?.PropertyId == propertyId ? _existing : null);

    public void Add(PropertyAccessConfiguration aggregate) => AddedConfigurations.Add(aggregate);
    public void Update(PropertyAccessConfiguration aggregate) => UpdatedConfigurations.Add(aggregate);
    public void Remove(PropertyAccessConfiguration aggregate) => throw new NotSupportedException("No delete endpoint exists — disabling is done via IsActive.");
}
