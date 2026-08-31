using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the current Reservation's own administrative summary (Fase 11,
/// Checkpoint 3) — reuses Reservations' existing <see cref="GetReservationDetailQuery"/>
/// through <see cref="IReservationsRequestDispatcher"/> (Exception #3), never
/// a new Contracts-tier reader. Zero arguments — <see cref="AgentToolContext.ReservationId"/>
/// is the only input, always backend-derived.
///
/// Deliberately excludes <see cref="ReservationResult.GuestName"/>/
/// <see cref="ReservationResult.GuestPhone"/>/<see cref="ReservationResult.GuestCount"/>
/// and every internal audit timestamp — only Status/CheckInAt/CheckOutAt/
/// PropertyId, the fields a guest-facing summary may safely echo back.
/// </summary>
public sealed class GetReservationSummaryTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetReservationSummary,
        "Retorna o status atual da reserva do hóspede, incluindo datas de check-in e check-out.");

    private readonly IReservationsRequestDispatcher _dispatcher;

    public GetReservationSummaryTool(IReservationsRequestDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var result = await _dispatcher.Send(new GetReservationDetailQuery(context.ReservationId), cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        var reservation = result.Value;
        var content =
            $"Status da reserva: {reservation.Status}. " +
            $"Check-in: {reservation.CheckInAt:yyyy-MM-dd HH:mm} UTC. " +
            $"Check-out: {reservation.CheckOutAt:yyyy-MM-dd HH:mm} UTC. " +
            $"Identificador da propriedade: {reservation.PropertyId}.";

        return AgentToolResult.Success(content);
    }
}
