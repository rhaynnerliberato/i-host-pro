using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Tools;

/// <summary>
/// Reads the Reservation's own property access instructions (Fase 11,
/// Checkpoint 3) — reuses PropertyManagement's existing
/// <see cref="GetPropertyAccessConfigurationQuery"/> through
/// <see cref="IPropertyManagementRequestDispatcher"/> (Exception #3) — never
/// <c>IPropertyGuestAccessReader</c>, which stays purpose-limited to
/// Communication only (ADR-028). Zero arguments —
/// <see cref="AgentToolContext.ReservationId"/> resolves the PropertyId first
/// (via Reservations' own <see cref="GetReservationDetailQuery"/>).
///
/// Returns ONLY <see cref="PropertyAccessConfigurationResult.AccessInstructions"/>
/// — never <see cref="PropertyAccessConfigurationResult.AccessCredentialSecretReference"/>,
/// and never resolves the actual credential value. WiFi/parking/rules are
/// explicitly out of scope this checkpoint (no structured source exists yet)
/// — this tool never assumes <c>AccessInstructions</c> covers them; it
/// returns whatever free-text the administrator configured, verbatim.
/// </summary>
public sealed class GetAccessInstructionsTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        AgentToolNames.GetAccessInstructions,
        "Retorna as instruções de acesso à propriedade configuradas pelo administrador (nunca a credencial em si).");

    private readonly IReservationsRequestDispatcher _reservationsDispatcher;
    private readonly IPropertyManagementRequestDispatcher _propertyManagementDispatcher;

    public GetAccessInstructionsTool(
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

        var accessResult = await _propertyManagementDispatcher.Send(
            new GetPropertyAccessConfigurationQuery(reservationResult.Value.PropertyId), cancellationToken);
        if (accessResult.IsFailure)
            return AgentToolResult.Failure(accessResult.Error.Code);

        var configuration = accessResult.Value;
        if (!configuration.IsActive || string.IsNullOrWhiteSpace(configuration.AccessInstructions))
            return AgentToolResult.Failure("access_instructions_not_available");

        return AgentToolResult.Success(configuration.AccessInstructions);
    }
}
