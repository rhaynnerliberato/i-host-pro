using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Infrastructure.AIAgent;

/// <inheritdoc cref="IConversationHistoryReader"/>
/// <remarks>
/// The only implementation permitted to exist for
/// <see cref="IConversationHistoryReader"/> (Fase 11, Checkpoint 2 — ADR-030,
/// synchronous exception #14) — lives in <c>Communication.Infrastructure</c>,
/// the one layer allowed to touch <see cref="CommunicationDbContext"/>
/// directly. Mirrors <c>ReservationByGuestPhoneReader</c>'s own structural
/// precedent exactly (ADR-029): its own short-lived, read-only, tenant-scoped
/// transaction via <see cref="TenantAwareTransactionScope"/>, a throwaway
/// local <see cref="TenantContext"/>, no cache, no mutation.
///
/// Content safety (ADR-030 item 4/item 12 governance resolution): a message
/// whose persisted <see cref="Message.RenderedContent"/> is already the
/// fixed <c>"[SENSITIVE CONTENT REDACTED]"</c> marker (Guest Access
/// credential delivery, ADR-028) is returned as-is — no reconstruction is
/// possible since the real content was never persisted in the first place.
/// A PIX delivery message (<see cref="PixDeliveryTemplateKey"/>) is DIFFERENT:
/// ADR-025/ADR-027 deliberately renders the real QR/copy-paste payload
/// directly into <see cref="Message.RenderedContent"/> (the guest's intended
/// final destination for it) — so THIS reader, not the write side, is the
/// enforcement point that redacts it before it can ever reach the AI Agent,
/// mirroring the exact same marker Guest Access already uses at write time.
/// This is a read-side-only decision — Fase 10's own already-homologated PIX
/// delivery persistence is untouched.
/// </remarks>
public sealed class ConversationHistoryReader : IConversationHistoryReader
{
    private const string SensitiveContentMarker = "[SENSITIVE CONTENT REDACTED]";
    private const string PixDeliveryTemplateKey = "LATE_CHECKOUT_PIX_PAYMENT";
    private const string Purpose = "ai_agent_conversation_history";
    private const string Caller = "AIAgent";

    private readonly CommunicationDbContext _dbContext;
    private readonly ILogger<ConversationHistoryReader> _logger;

    public ConversationHistoryReader(CommunicationDbContext dbContext, ILogger<ConversationHistoryReader> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConversationHistoryMessage>> GetHistoryAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var rows = await _dbContext.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAtUtc)
            .ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.Direction, m.TemplateKey, m.RenderedContent, m.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var history = rows
            .Select(m => new ConversationHistoryMessage(
                m.Id,
                m.Direction == MessageDirection.Inbound ? ConversationMessageDirection.Inbound : ConversationMessageDirection.Outbound,
                RedactIfSensitive(m.TemplateKey, m.RenderedContent),
                m.CreatedAtUtc))
            .ToList();

        _logger.LogInformation(
            "Conversation history read for {Purpose} by {Caller}: tenant {TenantId} conversationId {ConversationId} — {MessageCount} message(s)",
            Purpose, Caller, tenantId, conversationId, history.Count);

        return history;
    }

    private static string RedactIfSensitive(string templateKey, string renderedContent) =>
        templateKey == PixDeliveryTemplateKey ? SensitiveContentMarker : renderedContent;
}
