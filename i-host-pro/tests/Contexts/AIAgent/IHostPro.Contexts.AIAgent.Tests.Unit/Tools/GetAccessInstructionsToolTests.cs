using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetAccessInstructionsToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ReservationResult BuildReservation() => new(
        Context.ReservationId, PropertyId, "Guest", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), 2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExecuteAsync_returns_only_the_instructions_text_never_the_credential_reference()
    {
        var reservationsDispatcher = new FakeReservationsRequestDispatcher();
        reservationsDispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        var propertyManagementDispatcher = new FakePropertyManagementRequestDispatcher();
        var configuration = new PropertyAccessConfigurationResult(
            Guid.NewGuid(), PropertyId, "vault://secret-reference-should-never-leak",
            "Use o código 1234 no portão principal.", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        propertyManagementDispatcher.Stub.SetResponse(new GetPropertyAccessConfigurationQuery(PropertyId), Result.Success(configuration));
        var tool = new GetAccessInstructionsTool(reservationsDispatcher, propertyManagementDispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Be("Use o código 1234 no portão principal.");
        result.Content.Should().NotContain("vault://", "the credential secret reference must never leave this tool");
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_the_configuration_is_inactive()
    {
        var reservationsDispatcher = new FakeReservationsRequestDispatcher();
        reservationsDispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        var propertyManagementDispatcher = new FakePropertyManagementRequestDispatcher();
        var configuration = new PropertyAccessConfigurationResult(
            Guid.NewGuid(), PropertyId, null, "Instruções antigas", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        propertyManagementDispatcher.Stub.SetResponse(new GetPropertyAccessConfigurationQuery(PropertyId), Result.Success(configuration));
        var tool = new GetAccessInstructionsTool(reservationsDispatcher, propertyManagementDispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("access_instructions_not_available");
    }
}
