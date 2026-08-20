using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// Thin read-only adapter over <see cref="IWhatsAppTenantRouteRepository"/>
/// (Fase 9, Checkpoint 2.3.2) — never a Meta-specific concern (routing by
/// PhoneNumberId is WhatsApp-integration vocabulary, not Graph API wire
/// format), so this lives in Infrastructure root, not <c>.Meta</c>.
/// </summary>
public sealed class WhatsAppTenantRouteResolver : IWhatsAppTenantRouteResolver
{
    private readonly IWhatsAppTenantRouteRepository _repository;

    public WhatsAppTenantRouteResolver(IWhatsAppTenantRouteRepository repository) => _repository = repository;

    public async Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken)
    {
        var route = await _repository.GetByPhoneNumberIdAsync(phoneNumberId, cancellationToken);
        return route?.TenantId;
    }
}
