using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.AIAgent;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — ADR-030, synchronous
/// exception #14, mandate item 34. Reuses <see cref="CommunicationMessageExecutionScopeTests.Fixture"/> —
/// same container, same migrated schema, no new fixture needed.
/// </summary>
public class ConversationHistoryReaderTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private const string PixDeliveryTemplateKey = "LATE_CHECKOUT_PIX_PAYMENT";
    private const string SensitiveContentMarker = "[SENSITIVE CONTENT REDACTED]";

    private readonly CommunicationMessageExecutionScopeTests.Fixture _fixture;

    public ConversationHistoryReaderTests(CommunicationMessageExecutionScopeTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetHistoryAsync_returns_messages_for_the_correct_tenant_only()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedOutboundAsync(tenantId, conversationId, reservationId, "Sua reserva foi confirmada.", DateTimeOffset.UtcNow);

        var history = await ReadAsync(tenantId, conversationId);
        var historyForOtherTenant = await ReadAsync(otherTenantId, conversationId);

        history.Should().ContainSingle();
        historyForOtherTenant.Should().BeEmpty("RLS must isolate the history per tenant, even for a known conversationId");
    }

    [Fact]
    public async Task GetHistoryAsync_returns_empty_for_a_nonexistent_Conversation()
    {
        var history = await ReadAsync(Guid.NewGuid(), Guid.NewGuid());

        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_returns_messages_in_chronological_order()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        await SeedOutboundAsync(tenantId, conversationId, reservationId, "primeira", t0);
        await SeedInboundAsync(tenantId, conversationId, reservationId, "segunda", t0.AddMinutes(1));
        await SeedOutboundAsync(tenantId, conversationId, reservationId, "terceira", t0.AddMinutes(2));

        var history = await ReadAsync(tenantId, conversationId);

        history.Should().HaveCount(3);
        history.Select(m => m.Content).Should().ContainInOrder("primeira", "segunda", "terceira");
        history.Select(m => m.OccurredAtUtc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetHistoryAsync_returns_inbound_guest_text_and_marks_direction_correctly()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedInboundAsync(tenantId, conversationId, reservationId, "Olá, preciso de ajuda", DateTimeOffset.UtcNow);

        var history = await ReadAsync(tenantId, conversationId);

        history.Should().ContainSingle();
        history[0].Direction.Should().Be(ConversationMessageDirection.Inbound);
        history[0].Content.Should().Be("Olá, preciso de ajuda");
    }

    [Fact]
    public async Task GetHistoryAsync_returns_normal_outbound_content_and_marks_direction_correctly()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedOutboundAsync(tenantId, conversationId, reservationId, "Sua reserva foi confirmada.", DateTimeOffset.UtcNow);

        var history = await ReadAsync(tenantId, conversationId);

        history.Should().ContainSingle();
        history[0].Direction.Should().Be(ConversationMessageDirection.Outbound);
        history[0].Content.Should().Be("Sua reserva foi confirmada.");
    }

    [Fact]
    public async Task GetHistoryAsync_preserves_the_sensitive_redacted_marker_exactly_never_reconstructing_it()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedOutboundAsync(tenantId, conversationId, reservationId, SensitiveContentMarker, DateTimeOffset.UtcNow, templateKey: "GUEST_ACCESS_CREDENTIAL");

        var history = await ReadAsync(tenantId, conversationId);

        history.Should().ContainSingle();
        history[0].Content.Should().Be(SensitiveContentMarker);
    }

    [Fact]
    public async Task GetHistoryAsync_redacts_a_PIX_delivery_messages_real_QR_content_never_leaking_it()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        const string realQrPayload = "00020126FAKE-PIX-NO-REAL-MONEY-ABC123-100.00-BRL6304FAKE";
        await SeedOutboundAsync(tenantId, conversationId, reservationId, realQrPayload, DateTimeOffset.UtcNow, templateKey: PixDeliveryTemplateKey);

        var history = await ReadAsync(tenantId, conversationId);

        history.Should().ContainSingle();
        history[0].Content.Should().Be(SensitiveContentMarker, "the real QR/copy-paste payload is legitimately persisted in RenderedContent (ADR-025/ADR-027) but must never reach the AI Agent");
        history[0].Content.Should().NotContain("FAKE-PIX-NO-REAL-MONEY");
    }

    [Fact]
    public async Task GetHistoryAsync_returns_only_the_minimal_projection_no_provider_metadata()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedOutboundAsync(tenantId, conversationId, reservationId, "Olá", DateTimeOffset.UtcNow);

        var history = await ReadAsync(tenantId, conversationId);

        var propertyNames = typeof(ConversationHistoryMessage).GetProperties().Select(p => p.Name).ToList();
        propertyNames.Should().BeEquivalentTo(["MessageId", "Direction", "Content", "OccurredAtUtc"]);
    }

    // ---- Seeding --------------------------------------------------------

    private async Task SeedOutboundAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, string content, DateTimeOffset createdAtUtc, string templateKey = "RESERVATION_CONFIRMATION")
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var message = Message.Create(
            Guid.NewGuid(), tenantId, conversationId, reservationId, "WhatsApp", templateKey,
            null, content, $"idem-{Guid.NewGuid():N}", createdAtUtc);

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedInboundAsync(Guid tenantId, Guid conversationId, Guid reservationId, string content, DateTimeOffset receivedAtUtc)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var message = Message.CreateInbound(
            Guid.NewGuid(), tenantId, conversationId, reservationId, "WhatsApp",
            content, $"wamid.{Guid.NewGuid():N}", $"idem-{Guid.NewGuid():N}", receivedAtUtc);

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<IReadOnlyList<ConversationHistoryMessage>> ReadAsync(Guid tenantId, Guid conversationId)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        var reader = new ConversationHistoryReader(dbContext, NullLogger<ConversationHistoryReader>.Instance);
        return await reader.GetHistoryAsync(tenantId, conversationId, CancellationToken.None);
    }

    private static TenantContext NewTenantScopedContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return tenantContext;
    }

    private static async Task SetTenantAsync(CommunicationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private CommunicationDbContext CreateDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }
}
