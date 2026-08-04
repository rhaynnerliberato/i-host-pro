using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

public class GetCondominiumDetailQueryHandlerTests
{
    private static readonly AddressResult SomeAddress = new(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    [Fact]
    public async Task An_existing_condominium_returns_its_full_detail()
    {
        var condominiumId = Guid.NewGuid();
        var expected = new CondominiumResult(condominiumId, "Condomínio Exemplo", SomeAddress, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var reader = FakeCondominiumReader.WithDetail(expected);
        var handler = new GetCondominiumDetailQueryHandler(reader);

        var result = await handler.Handle(new GetCondominiumDetailQuery(condominiumId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
        reader.LastRequestedId.Should().Be(condominiumId);
    }

    [Fact]
    public async Task A_nonexistent_condominium_fails_with_CondominiumNotFound()
    {
        var reader = FakeCondominiumReader.WithDetail(null);
        var handler = new GetCondominiumDetailQueryHandler(reader);

        var result = await handler.Handle(new GetCondominiumDetailQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var reader = FakeCondominiumReader.WithDetail(null);
        var handler = new GetCondominiumDetailQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        var act = async () => await handler.Handle(new GetCondominiumDetailQuery(Guid.NewGuid()), cts.Token);

        await act.Should().NotThrowAsync();
    }
}
