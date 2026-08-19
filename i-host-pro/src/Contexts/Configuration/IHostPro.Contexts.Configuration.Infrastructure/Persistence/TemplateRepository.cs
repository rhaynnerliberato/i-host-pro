using IHostPro.Contexts.Configuration.Application.Templates;
using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

public sealed class TemplateRepository : ITemplateRepository
{
    private readonly ConfigurationDbContext _dbContext;

    public TemplateRepository(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public Task<Template?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Templates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<Template?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        _dbContext.Templates.FirstOrDefaultAsync(t => t.Key == key, cancellationToken);

    public void Add(Template aggregate) => _dbContext.Templates.Add(aggregate);

    public void Update(Template aggregate) => _dbContext.Templates.Update(aggregate);

    public void Remove(Template aggregate) => _dbContext.Templates.Remove(aggregate);
}
