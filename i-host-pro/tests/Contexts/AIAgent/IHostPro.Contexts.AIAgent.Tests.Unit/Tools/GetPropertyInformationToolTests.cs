using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetPropertyInformationToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ReservationResult BuildReservation() => new(
        Context.ReservationId, PropertyId, "Guest", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), 2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static PropertyResult BuildProperty()
    {
        var address = new AddressResult("00000-000", "Rua A", "100", null, "Centro", "São Paulo", "SP", "BR");
        return new PropertyResult(PropertyId, "COD1", "Casa da Praia", 6, null, null, address, "property", "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExecuteAsync_resolves_PropertyId_from_the_reservation_and_returns_guest_appropriate_fields()
    {
        var reservationsDispatcher = new FakeReservationsRequestDispatcher();
        reservationsDispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        var propertyManagementDispatcher = new FakePropertyManagementRequestDispatcher();
        propertyManagementDispatcher.Stub.SetResponse(new GetPropertyDetailQuery(PropertyId), Result.Success(BuildProperty()));
        var tool = new GetPropertyInformationTool(reservationsDispatcher, propertyManagementDispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("Casa da Praia");
        result.Content.Should().Contain("São Paulo");
        var propertyRequest = propertyManagementDispatcher.Stub.ReceivedRequests.OfType<GetPropertyDetailQuery>().Single();
        propertyRequest.PropertyId.Should().Be(PropertyId);
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_reservation_lookup_failure_without_calling_property_management()
    {
        var reservationsDispatcher = new FakeReservationsRequestDispatcher();
        reservationsDispatcher.Stub.SetResponse(
            new GetReservationDetailQuery(Context.ReservationId), Result.Failure<ReservationResult>(new Error("reservation_not_found", "reservation_not_found")));
        var propertyManagementDispatcher = new FakePropertyManagementRequestDispatcher();
        var tool = new GetPropertyInformationTool(reservationsDispatcher, propertyManagementDispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("reservation_not_found");
        propertyManagementDispatcher.Stub.ReceivedRequests.Should().BeEmpty();
    }
}
