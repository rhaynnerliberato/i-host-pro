using IHostPro.Contexts.Configuration.Application.Templates;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Templates;

internal sealed class FakeTemplateRepository : ITemplateRepository
{
    private readonly Dictionary<string, Template> _byKey = new();

    public static FakeTemplateRepository WithExisting(Template? existing)
    {
        var repository = new FakeTemplateRepository();
        if (existing is not null)
            repository._byKey[existing.Key] = existing;
        return repository;
    }

    public List<Template> AddedTemplates { get; } = [];
    public List<Template> UpdatedTemplates { get; } = [];

    public Task<Template?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byKey.Values.FirstOrDefault(t => t.Id == id));

    public Task<Template?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_byKey.GetValueOrDefault(key));

    public void Add(Template aggregate)
    {
        _byKey[aggregate.Key] = aggregate;
        AddedTemplates.Add(aggregate);
    }

    public void Update(Template aggregate)
    {
        _byKey[aggregate.Key] = aggregate;
        UpdatedTemplates.Add(aggregate);
    }

    public void Remove(Template aggregate) => _byKey.Remove(aggregate.Key);
}
