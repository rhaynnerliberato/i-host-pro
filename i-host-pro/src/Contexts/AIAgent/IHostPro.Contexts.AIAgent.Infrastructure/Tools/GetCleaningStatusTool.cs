using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the current cleaning status for the Reservation (Fase 11, Checkpoint
/// 3) — reuses Housekeeping's existing <see cref="GetCleaningStatusByReservationQuery"/>
/// through <see cref="IHousekeepingRequestDispatcher"/> (Exception #3). Zero
/// arguments — <see cref="AgentToolContext.ReservationId"/> is the only
/// input, always backend-derived. Only real persisted facts — never an
/// invented ETA/estimate.
/// </summary>
public sealed class GetCleaningStatusTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetCleaningStatus,
        "Retorna o status atual da faxina associada à reserva do hóspede.");

    private readonly IHousekeepingRequestDispatcher _dispatcher;

    public GetCleaningStatusTool(IHousekeepingRequestDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var result = await _dispatcher.Send(
            new GetCleaningStatusByReservationQuery(context.ReservationId), cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        var status = result.Value;
        var content = $"Status da faxina: {status.Status}.";
        if (status.ScheduledAtUtc is not null)
            content += $" Agendada para {status.ScheduledAtUtc:yyyy-MM-dd HH:mm} UTC.";
        if (status.CompletedAtUtc is not null)
            content += $" Concluída em {status.CompletedAtUtc:yyyy-MM-dd HH:mm} UTC.";

        return AgentToolResult.Success(content);
    }
}
