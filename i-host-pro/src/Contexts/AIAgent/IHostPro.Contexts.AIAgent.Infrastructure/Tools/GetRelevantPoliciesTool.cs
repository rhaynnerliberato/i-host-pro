using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the effective Early Check-in/Late Checkout policy (or both) for the
/// Reservation's own property (Fase 11, Checkpoint 3) — reuses
/// Configuration's existing <see cref="GetEffectivePolicyQuery"/> through
/// <see cref="IConfigurationRequestDispatcher"/> (Exception #3).
///
/// The optional <c>"policyCode"</c> argument is validated against the only
/// two real codes (<see cref="EarlyCheckInCode"/>/<see cref="LateCheckoutCode"/>)
/// — an invalid code fails rather than silently falling back. When omitted,
/// both policies are read and summarized. <see cref="EffectivePolicyResult.Value"/>
/// (a boxed <c>object?</c> in the underlying query, since it is generic over
/// whichever code the route names) is always cast to its known concrete type
/// here — never passed through untyped — building a typed-safe, guest-facing
/// summary. This tool informs facts only; it never implements approval/
/// eligibility logic (that remains GuestOperations' own Request flow).
/// </summary>
public sealed class GetRelevantPoliciesTool : IAgentTool
{
    public const string EarlyCheckInCode = "EARLY_CHECKIN";
    public const string LateCheckoutCode = "LATE_CHECKOUT";

    private static readonly IReadOnlyList<string> AllowedCodes = [EarlyCheckInCode, LateCheckoutCode];

    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetRelevantPolicies,
        "Retorna as políticas efetivas de early check-in e/ou late checkout da propriedade da reserva. Argumento opcional \"policyCode\" (EARLY_CHECKIN ou LATE_CHECKOUT); se omitido, retorna ambas.");

    private readonly IReservationsRequestDispatcher _reservationsDispatcher;
    private readonly IConfigurationRequestDispatcher _configurationDispatcher;

    public GetRelevantPoliciesTool(
        IReservationsRequestDispatcher reservationsDispatcher, IConfigurationRequestDispatcher configurationDispatcher)
    {
        _reservationsDispatcher = reservationsDispatcher;
        _configurationDispatcher = configurationDispatcher;
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        string[] codesToRead;
        if (arguments is not null && arguments.TryGetValue("policyCode", out var requestedCode))
        {
            if (!AllowedCodes.Contains(requestedCode))
                return AgentToolResult.Failure("invalid_policy_code");

            codesToRead = [requestedCode];
        }
        else
        {
            codesToRead = [EarlyCheckInCode, LateCheckoutCode];
        }

        var reservationResult = await _reservationsDispatcher.Send(
            new GetReservationDetailQuery(context.ReservationId), cancellationToken);
        if (reservationResult.IsFailure)
            return AgentToolResult.Failure(reservationResult.Error.Code);

        var summaries = new List<string>();
        foreach (var code in codesToRead)
        {
            var policyResult = await _configurationDispatcher.Send(
                new GetEffectivePolicyQuery(context.TenantId, code, reservationResult.Value.PropertyId), cancellationToken);
            if (policyResult.IsFailure)
                return AgentToolResult.Failure(policyResult.Error.Code);

            summaries.Add(Summarize(policyResult.Value));
        }

        return AgentToolResult.Success(string.Join('\n', summaries));
    }

    private static string Summarize(EffectivePolicyResult result)
    {
        if (result.Status != PolicyReadStatus.Resolved)
            return $"{result.PolicyCode}: não configurada.";

        return result.PolicyCode switch
        {
            EarlyCheckInCode when result.Value is EarlyCheckInPolicy earlyCheckIn => SummarizeEarlyCheckIn(earlyCheckIn),
            LateCheckoutCode when result.Value is LateCheckoutPolicy lateCheckout => SummarizeLateCheckout(lateCheckout),
            _ => $"{result.PolicyCode}: não configurada.",
        };
    }

    private static string SummarizeEarlyCheckIn(EarlyCheckInPolicy policy)
    {
        if (!policy.Allowed)
            return $"{EarlyCheckInCode}: não permitido.";

        var earliestTime = policy.EarliestTime is { } time ? time.ToString("HH:mm") : "sem horário mínimo definido";
        return $"{EarlyCheckInCode}: permitido a partir de {earliestTime}. " +
               $"Requer faxina concluída: {(policy.RequiresCleaningCompleted ? "sim" : "não")}. " +
               $"Requer formulário: {(policy.RequiresForm ? "sim" : "não")}.";
    }

    private static string SummarizeLateCheckout(LateCheckoutPolicy policy)
    {
        if (!policy.Allowed)
            return $"{LateCheckoutCode}: não permitido.";

        var latestTime = policy.LatestTime is { } time ? time.ToString("HH:mm") : "sem horário máximo definido";
        var charge = policy.ChargeType switch
        {
            Configuration.Contracts.LateCheckoutChargeType.None => "sem cobrança",
            Configuration.Contracts.LateCheckoutChargeType.FixedAmount => $"cobrança fixa de {policy.ChargeValue}",
            Configuration.Contracts.LateCheckoutChargeType.Percentage => $"cobrança de {policy.ChargeValue}%",
            _ => "cobrança não especificada",
        };

        return $"{LateCheckoutCode}: permitido até {latestTime}. Cobrança: {charge}. " +
               $"Requer PIX: {(policy.RequiresPix ? "sim" : "não")}.";
    }
}
