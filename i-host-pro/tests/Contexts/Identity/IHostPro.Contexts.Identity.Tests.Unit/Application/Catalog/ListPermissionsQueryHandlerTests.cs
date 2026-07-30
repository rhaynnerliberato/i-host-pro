using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Catalog;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Catalog;

public class ListPermissionsQueryHandlerTests
{
    private static readonly CatalogPermission[] SamplePermissions =
    [
        new CatalogPermission("USERS:MANAGE", "USERS", "MANAGE", null),
    ];

    [Fact]
    public async Task Calls_the_reader_exactly_once()
    {
        var reader = FakeIdentityCatalogReader.WithPermissions(SamplePermissions);
        var handler = new ListPermissionsQueryHandler(reader);

        await handler.Handle(new ListPermissionsQuery(), CancellationToken.None);

        reader.ListPermissionsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Propagates_the_readers_result_unchanged()
    {
        var reader = FakeIdentityCatalogReader.WithPermissions(SamplePermissions);
        var handler = new ListPermissionsQueryHandler(reader);

        var result = await handler.Handle(new ListPermissionsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(SamplePermissions);
    }

    [Fact]
    public async Task An_empty_collection_from_the_reader_is_a_valid_success_result()
    {
        var reader = FakeIdentityCatalogReader.WithPermissions([]);
        var handler = new ListPermissionsQueryHandler(reader);

        var result = await handler.Handle(new ListPermissionsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_exception_propagates_and_is_never_converted_into_an_empty_success()
    {
        var reader = FakeIdentityCatalogReader.ThatThrows(new InvalidOperationException("simulated PostgreSQL failure"));
        var handler = new ListPermissionsQueryHandler(reader);

        var act = () => handler.Handle(new ListPermissionsQuery(), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_reader()
    {
        var reader = FakeIdentityCatalogReader.WithPermissions([]);
        var handler = new ListPermissionsQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new ListPermissionsQuery(), cts.Token);

        reader.LastCancellationToken.Should().Be(cts.Token);
    }
}
