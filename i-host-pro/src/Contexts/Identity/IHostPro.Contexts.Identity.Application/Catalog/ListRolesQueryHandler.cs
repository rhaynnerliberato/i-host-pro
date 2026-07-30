using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application.Catalog;

/// <summary>
/// Delegates directly to <see cref="IIdentityCatalogReader"/> — no business
/// rule can reject this query, so it always returns <see cref="Result.Success{TValue}"/>
/// (Incremento 3, Checkpoint 3). A reader failure is never caught here: it
/// propagates unchanged, so it can never be mistaken for "the catalog is
/// empty".
/// </summary>
public sealed class ListRolesQueryHandler : IQueryHandler<ListRolesQuery, IReadOnlyCollection<CatalogRole>>
{
    private readonly IIdentityCatalogReader _catalogReader;

    public ListRolesQueryHandler(IIdentityCatalogReader catalogReader) => _catalogReader = catalogReader;

    public async ValueTask<Result<IReadOnlyCollection<CatalogRole>>> Handle(
        ListRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await _catalogReader.ListRolesAsync(cancellationToken);
        return Result.Success(roles);
    }
}
