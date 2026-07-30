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
public sealed class ListPermissionsQueryHandler : IQueryHandler<ListPermissionsQuery, IReadOnlyCollection<CatalogPermission>>
{
    private readonly IIdentityCatalogReader _catalogReader;

    public ListPermissionsQueryHandler(IIdentityCatalogReader catalogReader) => _catalogReader = catalogReader;

    public async ValueTask<Result<IReadOnlyCollection<CatalogPermission>>> Handle(
        ListPermissionsQuery query, CancellationToken cancellationToken)
    {
        var permissions = await _catalogReader.ListPermissionsAsync(cancellationToken);
        return Result.Success(permissions);
    }
}
