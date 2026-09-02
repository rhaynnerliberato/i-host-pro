using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.GuestOperations.Application;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Triggers the real secure guest access delivery choreography for the
/// Reservation (Fase 11, Checkpoint 4) — reuses Guest Operations' existing
/// <see cref="RequestGuestAccessDeliveryCommand"/> through
/// <see cref="IGuestOperationsRequestDispatcher"/> (Exception #3).
///
/// EXPLICIT_REQUEST_IS_CONFIRMATION (CP0 decision, reaffirmed by the CP4
/// mandate item 7): the guest's own explicit request ("me envie a senha")
/// already is sufficient confirmation — this Tool is a plain
/// <see cref="IAgentTool"/>, never <see cref="IConfirmableAgentTool"/>, and
/// the orchestrator executes it in the SAME turn it is proposed, no
/// <c>AgentPendingAction</c> involved. Zero arguments — <see cref="AgentToolContext.TenantId"/>/
/// <see cref="AgentToolContext.ReservationId"/> are the only inputs, always
/// backend-derived.
///
/// The real delivery itself happens asynchronously (the Command publishes
/// <c>GuestAccessDeliveryRequested</c>, consumed by Communication) — this
/// Tool's own result is therefore always about the REQUEST being accepted,
/// never about delivery completion. Never returns the credential/secret
/// reference — the Command itself never resolves it.
/// </summary>
public sealed class RequestGuestAccessDeliveryTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.RequestGuestAccessDelivery,
        "Solicita o envio das instruções/credencial de acesso à propriedade ao hóspede. Executa imediatamente — o próprio pedido explícito do hóspede já é a confirmação.");

    private readonly IGuestOperationsRequestDispatcher _guestOperationsDispatcher;

    public RequestGuestAccessDeliveryTool(IGuestOperationsRequestDispatcher guestOperationsDispatcher) =>
        _guestOperationsDispatcher = guestOperationsDispatcher;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var result = await _guestOperationsDispatcher.Send(
            new RequestGuestAccessDeliveryCommand
            {
                TenantId = context.TenantId,
                ReservationId = context.ReservationId,
                ActorType = "AI",
                ActorId = context.AgentSessionId,
            },
            cancellationToken);
        if (result.IsFailure)
            return AgentToolResult.Failure(result.Error.Code);

        return AgentToolResult.Success("Solicitação de envio de acesso registrada com sucesso.");
    }
}
