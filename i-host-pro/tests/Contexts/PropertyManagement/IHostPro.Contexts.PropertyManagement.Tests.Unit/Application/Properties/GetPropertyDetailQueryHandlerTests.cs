using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class GetPropertyDetailQueryHandlerTests
{
    private static readonly AddressResult SomeAddress = new(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    [Fact]
    public async Task An_existing_property_returns_its_full_detail()
    {
        var propertyId = Guid.NewGuid();
        var expected = new PropertyResult(
            propertyId, "STUDIO-1", "Studio 1", 2, null, SomeAddress, SomeAddress, "property", null, "draft",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var reader = FakePropertyReader.WithDetail(expected);
        var handler = new GetPropertyDetailQueryHandler(reader);

        var result = await handler.Handle(new GetPropertyDetailQuery(propertyId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
        reader.LastRequestedId.Should().Be(propertyId);
    }

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound()
    {
        var reader = FakePropertyReader.WithDetail(null);
        var handler = new GetPropertyDetailQueryHandler(reader);

        var result = await handler.Handle(new GetPropertyDetailQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var reader = FakePropertyReader.WithDetail(null);
        var handler = new GetPropertyDetailQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        var act = async () => await handler.Handle(new GetPropertyDetailQuery(Guid.NewGuid()), cts.Token);

        await act.Should().NotThrowAsync();
    }
}
