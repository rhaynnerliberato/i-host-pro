using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.GuestOperations.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Proposes/executes a Late Checkout request for the Reservation (Fase 11,
/// Checkpoint 4) — reuses Guest Operations' existing
/// <see cref="RequestLateCheckoutCommand"/> through
/// <see cref="IGuestOperationsRequestDispatcher"/> (Exception #3). Charge
/// amount/currency/PIX data are NEVER supplied by the model — always
/// resolved internally by the Command from the effective policy. PIX
/// generation, when required, remains the real Late Checkout choreography's
/// own consequence (<c>LateCheckoutPaymentRequired</c> → Payments) — this
/// Tool never calls Payments directly and never sees a QR payload.
///
/// CONFIRMATION_REQUIRED, mirrors <see cref="RequestEarlyCheckInTool"/>
/// exactly. <c>RequestedCheckOutAt</c> is the only model-derived argument.
/// A business outcome of <c>"denied"</c> or <c>"pending_payment"</c> is
/// still a successful Tool execution (CP4 mandate item 19/23).
/// </summary>
public sealed class RequestLateCheckoutTool : IConfirmableAgentTool
{
    private const string RequestedCheckOutAtKey = "requestedCheckOutAt";
    private const string MissingArgumentFailureCode = "missing_requested_check_out_at";
    private const string InvalidArgumentFailureCode = "invalid_requested_check_out_at";
    private const string PendingPaymentStatus = "pending_payment";

    /// <summary>Fase 11, Checkpoint 5 (mandate item 20) — see <see cref="RequestEarlyCheckInTool.OffsetQualifiedIso8601Pattern"/> for the full rationale; an offset-less input must be rejected, never silently interpreted using the server's own local timezone.</summary>
    private static readonly Regex OffsetQualifiedIso8601Pattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);

    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.RequestLateCheckout,
        "Propõe um pedido de late checkout para a reserva do hóspede. Argumento obrigatório \"requestedCheckOutAt\" (data/hora desejada). Exige confirmação explícita do hóspede antes de ser executado.");

    private readonly IGuestOperationsRequestDispatcher _guestOperationsDispatcher;

    public RequestLateCheckoutTool(IGuestOperationsRequestDispatcher guestOperationsDispatcher) =>
        _guestOperationsDispatcher = guestOperationsDispatcher;

    public AgentPendingActionProposalResult BuildSanitizedArguments(IReadOnlyDictionary<string, string>? arguments)
    {
        if (!TryParseRequestedCheckOutAt(arguments, out var requestedCheckOutAt, out var failureCode))
            return AgentPendingActionProposalResult.Failure(failureCode!);

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [RequestedCheckOutAtKey] = requestedCheckOutAt.ToString("O", CultureInfo.InvariantCulture),
        });
        return AgentPendingActionProposalResult.Success(json);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        if (!TryParseRequestedCheckOutAt(arguments, out var requestedCheckOutAt, out var failureCode))
            return AgentToolResult.Failure(failureCode!);

        var result = await _guestOperationsDispatcher.Send(
            new RequestLateCheckoutCommand
            {
                TenantId = context.TenantId,
                ReservationId = context.ReservationId,
                RequestedCheckOutAt = requestedCheckOutAt,
            },
            cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        var request = result.Value;
        var content = request.Status switch
        {
            "denied" when request.DenialReasonCode is not null => $"Pedido de late checkout: {request.Status}. Motivo: {request.DenialReasonCode}.",
            PendingPaymentStatus => $"Pedido de late checkout: {request.Status}. Uma cobrança foi gerada; o pagamento ainda precisa ser confirmado.",
            _ => $"Pedido de late checkout: {request.Status}.",
        };

        return AgentToolResult.Success(content);
    }

    private static bool TryParseRequestedCheckOutAt(
        IReadOnlyDictionary<string, string>? arguments, out DateTimeOffset requestedCheckOutAt, out string? failureCode)
    {
        requestedCheckOutAt = default;
        failureCode = null;

        if (arguments is null || !arguments.TryGetValue(RequestedCheckOutAtKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failureCode = MissingArgumentFailureCode;
            return false;
        }

        if (!OffsetQualifiedIso8601Pattern.IsMatch(raw)
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out requestedCheckOutAt))
        {
            failureCode = InvalidArgumentFailureCode;
            return false;
        }

        return true;
    }
}
