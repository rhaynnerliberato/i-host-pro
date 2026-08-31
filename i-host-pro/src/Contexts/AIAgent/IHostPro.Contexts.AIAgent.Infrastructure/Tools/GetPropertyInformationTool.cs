using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads guest-appropriate information about the Reservation's own property
/// (Fase 11, Checkpoint 3) — reuses PropertyManagement's existing
/// <see cref="GetPropertyDetailQuery"/> through <see cref="IPropertyManagementRequestDispatcher"/>
/// (Exception #3). Zero arguments — <see cref="AgentToolContext.ReservationId"/>
/// resolves the PropertyId first (via Reservations' own <see cref="GetReservationDetailQuery"/>,
/// <see cref="IReservationsRequestDispatcher"/>) — the model never supplies a
/// PropertyId directly.
///
/// Deliberately excludes anything from <c>PropertyAccessConfiguration</c>
/// (credential reference, access instructions — <see cref="GetAccessInstructionsTool"/>'s
/// own scope) and any Condominium/FrontDesk administrative detail — only
/// Name/EffectiveAddress/Capacity/Status.
/// </summary>
public sealed class GetPropertyInformationTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetPropertyInformation,
        "Retorna informações básicas da propriedade da reserva: nome, endereço e capacidade.");

    private readonly IReservationsRequestDispatcher _reservationsDispatcher;
    private readonly IPropertyManagementRequestDispatcher _propertyManagementDispatcher;

    public GetPropertyInformationTool(
        IReservationsRequestDispatcher reservationsDispatcher, IPropertyManagementRequestDispatcher propertyManagementDispatcher)
    {
        _reservationsDispatcher = reservationsDispatcher;
        _propertyManagementDispatcher = propertyManagementDispatcher;
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var reservationResult = await _reservationsDispatcher.Send(
            new GetReservationDetailQuery(context.ReservationId), cancellationToken);
        if (reservationResult.IsFailure)
            return AgentToolResult.Failure(reservationResult.Error.Code);

        var propertyResult = await _propertyManagementDispatcher.Send(
            new GetPropertyDetailQuery(reservationResult.Value.PropertyId), cancellationToken);
        if (propertyResult.IsFailure)
            return AgentToolResult.Failure(propertyResult.Error.Code);

        var property = propertyResult.Value;
        var address = property.EffectiveAddress;
        var content =
            $"Propriedade: {property.Name}. " +
            $"Endereço: {address.Street}, {address.Number}, {address.City}/{address.State}. " +
            $"Capacidade: {property.Capacity} hóspede(s).";

        return AgentToolResult.Success(content);
    }
}
