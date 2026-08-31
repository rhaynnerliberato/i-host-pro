using System.Globalization;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the Property's own unified Agenda (Reservation + Cleaning items) for
/// a short window (Fase 11, Checkpoint 3) — reuses Reservations' existing
/// <see cref="ListScheduleQuery"/> through <see cref="IReservationsRequestDispatcher"/>
/// (Exception #3). <see cref="AgentToolContext.ReservationId"/> resolves the
/// PropertyId first (via <see cref="GetReservationDetailQuery"/>) — the model
/// never supplies a PropertyId directly, and never a different property's.
///
/// The optional <c>"days"</c> argument is clamped to <see cref="MinDays"/>..<see cref="MaxDays"/>
/// (implementation decision, mirrors <c>ListScheduleQueryValidator</c>'s own
/// documented "explicit technical limit, not a requirement" precedent — this
/// tool's own window is deliberately far tighter than that validator's
/// 100-day administrative-calendar cap, since a conversational guest query
/// has no legitimate reason to span months). Never an arbitrary
/// multi-property query — always exactly the Reservation's own property.
/// </summary>
public sealed class GetScheduleTool : IAgentTool
{
    public const int DefaultDays = 7;
    public const int MinDays = 1;
    public const int MaxDays = 30;

    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetSchedule,
        "Retorna a agenda da propriedade (reservas e faxinas) para os próximos dias (argumento opcional \"days\", padrão 7, máximo 30).");

    private readonly IReservationsRequestDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    public GetScheduleTool(IReservationsRequestDispatcher dispatcher, TimeProvider timeProvider)
    {
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var reservationResult = await _dispatcher.Send(new GetReservationDetailQuery(context.ReservationId), cancellationToken);
        if (reservationResult.IsFailure)
            return AgentToolResult.Failure(reservationResult.Error.Code);

        var days = ResolveDays(arguments);
        var from = _timeProvider.GetUtcNow();
        var to = from.AddDays(days);

        var scheduleResult = await _dispatcher.Send(
            new ListScheduleQuery(from, to, reservationResult.Value.PropertyId, HousekeeperUserId: null, EventType: null),
            cancellationToken);
        if (scheduleResult.IsFailure)
            return AgentToolResult.Failure(scheduleResult.Error.Code);

        var items = scheduleResult.Value;
        if (items.Count == 0)
            return AgentToolResult.Success($"Nenhum evento agendado para a propriedade nos próximos {days} dia(s).");

        var lines = items.Select(item =>
            $"- {item.Type} em {item.StartAtUtc:yyyy-MM-dd HH:mm} UTC, status {item.Status}.");
        var content = $"Agenda da propriedade para os próximos {days} dia(s):\n{string.Join('\n', lines)}";

        return AgentToolResult.Success(content);
    }

    private static int ResolveDays(IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is not null
            && arguments.TryGetValue("days", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, MinDays, MaxDays);
        }

        return DefaultDays;
    }
}
