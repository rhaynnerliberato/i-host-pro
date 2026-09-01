using System.Globalization;
using System.Text.Json;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Proposes/executes an Early Check-in request for the Reservation (Fase 11,
/// Checkpoint 4) — reuses Guest Operations' existing
/// <see cref="RequestEarlyCheckInCommand"/> through
/// <see cref="IGuestOperationsRequestDispatcher"/> (Exception #3), never
/// duplicating the real policy/agenda/faxina evaluation the Command already
/// performs synchronously (ADR-024 B3/B4).
///
/// CONFIRMATION_REQUIRED (<see cref="IAgentToolConfirmationPolicy"/>): the
/// orchestrator calls <see cref="BuildSanitizedArguments"/> at proposal time
/// (never executes the Command yet) and <see cref="ExecuteAsync"/> only
/// after the guest confirms. <c>RequestedCheckInAt</c> is the ONLY
/// model-derived argument — <see cref="AgentToolContext.TenantId"/>/
/// <see cref="AgentToolContext.ReservationId"/> are always backend-derived,
/// never supplied by the model.
///
/// A business denial (<c>Status="denied"</c>) is still a successful Tool
/// execution (CP4 mandate item 19/23) — <see cref="AgentToolResult.Failure"/>
/// is reserved for genuine technical/precondition failures the Command
/// itself reports (e.g. Reservation not found/confirmed).
/// </summary>
public sealed class RequestEarlyCheckInTool : IConfirmableAgentTool
{
    private const string RequestedCheckInAtKey = "requestedCheckInAt";
    private const string MissingArgumentFailureCode = "missing_requested_check_in_at";
    private const string InvalidArgumentFailureCode = "invalid_requested_check_in_at";

    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.RequestEarlyCheckIn,
        "Propõe um pedido de early check-in para a reserva do hóspede. Argumento obrigatório \"requestedCheckInAt\" (data/hora desejada). Exige confirmação explícita do hóspede antes de ser executado.");

    private readonly IGuestOperationsRequestDispatcher _guestOperationsDispatcher;

    public RequestEarlyCheckInTool(IGuestOperationsRequestDispatcher guestOperationsDispatcher) =>
        _guestOperationsDispatcher = guestOperationsDispatcher;

    public AgentPendingActionProposalResult BuildSanitizedArguments(IReadOnlyDictionary<string, string>? arguments)
    {
        if (!TryParseRequestedCheckInAt(arguments, out var requestedCheckInAt, out var failureCode))
            return AgentPendingActionProposalResult.Failure(failureCode!);

        // Serialized as a plain Dictionary<string,string> — never a record/DTO
        // whose property-name casing could silently drift from the
        // dictionary key ExecuteAsync/TryParseRequestedCheckInAt look up
        // (round-tripped verbatim through AgentPendingAction.SanitizedArguments
        // by the orchestrator, back into the exact same IReadOnlyDictionary
        // shape ExecuteAsync already accepts — one argument shape, never two).
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [RequestedCheckInAtKey] = requestedCheckInAt.ToString("O", CultureInfo.InvariantCulture),
        });
        return AgentPendingActionProposalResult.Success(json);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        if (!TryParseRequestedCheckInAt(arguments, out var requestedCheckInAt, out var failureCode))
            return AgentToolResult.Failure(failureCode!);

        var result = await _guestOperationsDispatcher.Send(
            new RequestEarlyCheckInCommand
            {
                TenantId = context.TenantId,
                ReservationId = context.ReservationId,
                RequestedCheckInAt = requestedCheckInAt,
            },
            cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        var request = result.Value;
        var content = request.Status == "denied" && request.DenialReasonCode is not null
            ? $"Pedido de early check-in: {request.Status}. Motivo: {request.DenialReasonCode}."
            : $"Pedido de early check-in: {request.Status}.";

        return AgentToolResult.Success(content);
    }

    private static bool TryParseRequestedCheckInAt(
        IReadOnlyDictionary<string, string>? arguments, out DateTimeOffset requestedCheckInAt, out string? failureCode)
    {
        requestedCheckInAt = default;
        failureCode = null;

        if (arguments is null || !arguments.TryGetValue(RequestedCheckInAtKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failureCode = MissingArgumentFailureCode;
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out requestedCheckInAt))
        {
            failureCode = InvalidArgumentFailureCode;
            return false;
        }

        return true;
    }
}
