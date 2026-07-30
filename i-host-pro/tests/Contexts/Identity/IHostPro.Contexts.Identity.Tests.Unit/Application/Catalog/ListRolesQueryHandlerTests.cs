using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Catalog;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Catalog;

public class ListRolesQueryHandlerTests
{
    private static readonly CatalogRole[] SampleRoles =
    [
        new CatalogRole("ADMIN", "Administrador", ["USERS:MANAGE", "ROLES:READ"]),
    ];

    [Fact]
    public async Task Calls_the_reader_exactly_once()
    {
        var reader = FakeIdentityCatalogReader.WithRoles(SampleRoles);
        var handler = new ListRolesQueryHandler(reader);

        await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        reader.ListRolesCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Propagates_the_readers_result_unchanged()
    {
        var reader = FakeIdentityCatalogReader.WithRoles(SampleRoles);
        var handler = new ListRolesQueryHandler(reader);

        var result = await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(SampleRoles);
    }

    [Fact]
    public async Task An_empty_collection_from_the_reader_is_a_valid_success_result()
    {
        var reader = FakeIdentityCatalogReader.WithRoles([]);
        var handler = new ListRolesQueryHandler(reader);

        var result = await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Reader_exception_propagates_and_is_never_converted_into_an_empty_success()
    {
        var reader = FakeIdentityCatalogReader.ThatThrows(new InvalidOperationException("simulated PostgreSQL failure"));
        var handler = new ListRolesQueryHandler(reader);

        var act = () => handler.Handle(new ListRolesQuery(), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_token_is_propagated_to_the_reader()
    {
        var reader = FakeIdentityCatalogReader.WithRoles([]);
        var handler = new ListRolesQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        await handler.Handle(new ListRolesQuery(), cts.Token);

        reader.LastCancellationToken.Should().Be(cts.Token);
    }
}
