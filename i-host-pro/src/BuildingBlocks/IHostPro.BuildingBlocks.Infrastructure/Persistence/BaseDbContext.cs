using System.Linq.Expressions;
using System.Reflection;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Common conventions shared by every Bounded Context's DbContext: dedicated
/// PostgreSQL schema, a mandatory tenant isolation Global Query Filter applied
/// automatically to every entity implementing <see cref="ITenantOwned"/>, and the
/// <see cref="AuditSaveChangesInterceptor"/> extension point. Concrete contexts
/// (e.g. ReservationsDbContext) inherit from this class and only add their own
/// entities/mappings (Architecture Principles, Section 7).
/// </summary>
public abstract class BaseDbContext : DbContext, IModuleDbContext
{
    private static readonly MethodInfo BuildTenantFilterMethod = typeof(BaseDbContext)
        .GetMethod(nameof(BuildTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ITenantContext _tenantContext;

    public abstract string SchemaName { get; }

    protected BaseDbContext(DbContextOptions options, ITenantContext tenantContext) : base(options)
    {
        _tenantContext = tenantContext;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
                continue;

            var filter = (LambdaExpression)BuildTenantFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, null)!;

            entityType.SetQueryFilter(filter);
        }

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Builds `entity => entity.TenantId == _tenantContext.TenantId` for the given
    /// entity type. When the tenant has not been resolved yet (TenantId is null),
    /// the filter evaluates to false for every row — a fail-closed default, never
    /// fail-open (Engineering Principles §25, Security by Design).
    /// </summary>
    private LambdaExpression BuildTenantFilter<TEntity>()
        where TEntity : class, ITenantOwned
    {
        Expression<Func<TEntity, bool>> filter = entity => entity.TenantId == _tenantContext.TenantId;
        return filter;
    }
}
