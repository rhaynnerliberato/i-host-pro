using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Payments.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the current PIX payment status for the Reservation (Fase 11,
/// Checkpoint 3) — reuses Payments' existing <see cref="GetPaymentStatusByReservationQuery"/>
/// through <see cref="IPaymentsRequestDispatcher"/> (Exception #3). Zero
/// arguments — <see cref="AgentToolContext.ReservationId"/> is the only
/// input, always backend-derived.
///
/// <see cref="PaymentStatusResult.Status"/> is echoed verbatim
/// (Pending/Confirmed/Failed/Expired/Cancelled) — never an LLM-facing
/// interpreted conclusion. Deliberately excludes
/// QrCodePayload/ProviderChargeId/IdempotencyKey/payer data/provider-specific
/// failure detail.
/// </summary>
public sealed class GetPaymentStatusTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetPaymentStatus,
        "Retorna o status atual do pagamento PIX mais recente associado à reserva do hóspede.");

    private readonly IPaymentsRequestDispatcher _dispatcher;

    public GetPaymentStatusTool(IPaymentsRequestDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var result = await _dispatcher.Send(
            new GetPaymentStatusByReservationQuery(context.ReservationId), cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        var status = result.Value;
        var content =
            $"Status do pagamento: {status.Status}. " +
            $"Valor: {status.Amount} {status.CurrencyCode}.";
        if (status.ExpiresAtUtc is not null)
            content += $" Expira em {status.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC.";

        return AgentToolResult.Success(content);
    }
}
