using System.Globalization;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reports a plain CALENDAR free/busy fact for the Reservation's own
/// property over a short window (Fase 11, Checkpoint 3) — reuses
/// Reservations' existing <see cref="ListScheduleQuery"/> through
/// <see cref="IReservationsRequestDispatcher"/> (Exception #3), exactly the
/// same underlying data as <see cref="GetScheduleTool"/>, framed differently.
///
/// Deliberately means CALENDAR availability only — never Early Check-in/Late
/// Checkout eligibility, which stays GuestOperations' own Request flow.
/// Returns whether the property has any conflicting item in the requested
/// window, never an approval/eligibility conclusion and never a new business
/// rule invented on top of the raw schedule data.
/// </summary>
public sealed class GetAvailabilityTool : IAgentTool
{
    public const int DefaultDays = GetScheduleTool.DefaultDays;
    public const int MinDays = GetScheduleTool.MinDays;
    public const int MaxDays = GetScheduleTool.MaxDays;

    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetAvailability,
        "Informa se a propriedade tem algum evento de agenda (reserva ou faxina) conflitante nos próximos dias (argumento opcional \"days\", padrão 7, máximo 30). Não avalia elegibilidade de early check-in/late checkout.");

    private readonly IReservationsRequestDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;

    public GetAvailabilityTool(IReservationsRequestDispatcher dispatcher, TimeProvider timeProvider)
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
        var content = items.Count == 0
            ? $"A propriedade está livre de eventos de agenda nos próximos {days} dia(s)."
            : $"A propriedade tem {items.Count} evento(s) de agenda nos próximos {days} dia(s) (não é uma avaliação de elegibilidade de early check-in/late checkout).";

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
