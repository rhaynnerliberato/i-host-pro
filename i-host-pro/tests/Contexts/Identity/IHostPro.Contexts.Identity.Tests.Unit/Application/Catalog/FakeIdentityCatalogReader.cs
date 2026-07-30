using IHostPro.Contexts.Identity.Application.Catalog;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Catalog;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeIdentityCatalogReader : IIdentityCatalogReader
{
    private readonly IReadOnlyCollection<CatalogRole> _roles;
    private readonly IReadOnlyCollection<CatalogPermission> _permissions;
    private readonly Exception? _exceptionToThrow;

    private FakeIdentityCatalogReader(
        IReadOnlyCollection<CatalogRole> roles, IReadOnlyCollection<CatalogPermission> permissions, Exception? exceptionToThrow)
    {
        _roles = roles;
        _permissions = permissions;
        _exceptionToThrow = exceptionToThrow;
    }

    public static FakeIdentityCatalogReader WithRoles(IReadOnlyCollection<CatalogRole> roles) =>
        new(roles, [], exceptionToThrow: null);

    public static FakeIdentityCatalogReader WithPermissions(IReadOnlyCollection<CatalogPermission> permissions) =>
        new([], permissions, exceptionToThrow: null);

    public static FakeIdentityCatalogReader ThatThrows(Exception exception) => new([], [], exception);

    public int ListRolesCallCount { get; private set; }
    public int ListPermissionsCallCount { get; private set; }
    public CancellationToken? LastCancellationToken { get; private set; }

    public Task<IReadOnlyCollection<CatalogRole>> ListRolesAsync(CancellationToken cancellationToken)
    {
        ListRolesCallCount++;
        LastCancellationToken = cancellationToken;

        if (_exceptionToThrow is not null)
            throw _exceptionToThrow;

        return Task.FromResult(_roles);
    }

    public Task<IReadOnlyCollection<CatalogPermission>> ListPermissionsAsync(CancellationToken cancellationToken)
    {
        ListPermissionsCallCount++;
        LastCancellationToken = cancellationToken;

        if (_exceptionToThrow is not null)
            throw _exceptionToThrow;

        return Task.FromResult(_permissions);
    }
}
