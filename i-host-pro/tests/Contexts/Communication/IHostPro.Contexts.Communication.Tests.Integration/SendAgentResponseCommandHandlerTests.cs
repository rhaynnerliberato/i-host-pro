using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 4 (Write Tools & Response Delivery) — drives
/// <see cref="SendAgentResponseCommand"/> through the real
/// <see cref="ICommunicationRequestDispatcher"/> against a real PostgreSQL
/// instance. Reuses <see cref="CommunicationMessageExecutionScopeTests.Fixture"/>
/// — same container, same migrated schema, no new fixture needed. Uses a
/// fake <see cref="IReservationGuestContactReader"/>/<see cref="IOutboundMessageConnector"/>
/// — the real cross-context round trip is proven by the E2E suite.
/// </summary>
public class SendAgentResponseCommandHandlerTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private const string MessagingSchema = "communication_messaging";
    private const string Channel = "WhatsApp";

    private readonly CommunicationMessageExecutionScopeTests.Fixture _fixture;

    public SendAgentResponseCommandHandlerTests(CommunicationMessageExecutionScopeTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Send_creates_a_real_outbound_Message_and_returns_its_id()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        var guestContact = new ReservationGuestContact(reservationId, "+5511999998888", "Ana Silva");
        using var host = BuildHost(guestContact);

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Sua reserva está confirmada.");

        result.IsFailure.Should().BeFalse();
        var message = await ReadMessageAsync(tenantId, result.Value.MessageId);
        message.Should().NotBeNull();
        message!.Status.Should().Be(MessageStatus.Sent);
        message.Direction.Should().Be(MessageDirection.Outbound);
        message.ConversationId.Should().Be(conversationId);
        message.ReservationId.Should().Be(reservationId);
        message.Channel.Should().Be(Channel);
        message.TemplateKey.Should().Be("AI_AGENT_RESPONSE");
        message.RenderedContent.Should().Be("Sua reserva está confirmada.");
    }

    [Fact]
    public async Task Send_masks_the_guest_phone_never_persisting_it_in_full()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        var guestContact = new ReservationGuestContact(reservationId, "+5511999998888", "Ana Silva");
        using var host = BuildHost(guestContact);

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Olá");

        var message = await ReadMessageAsync(tenantId, result.Value.MessageId);
        message!.DestinationMasked.Should().NotBeNull();
        message.DestinationMasked.Should().NotContain("+5511999998888");
        message.DestinationMasked.Should().EndWith("8888");
    }

    [Fact]
    public async Task Send_is_idempotent_for_the_same_AgentInteractionId_returning_the_same_MessageId()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        var agentInteractionId = Guid.NewGuid();
        var guestContact = new ReservationGuestContact(reservationId, "+5511999998888", "Ana Silva");
        using var host = BuildHost(guestContact);

        var first = await SendAsync(host, tenantId, conversationId, reservationId, agentInteractionId, "Primeira tentativa");
        var second = await SendAsync(host, tenantId, conversationId, reservationId, agentInteractionId, "Primeira tentativa");

        first.Value.MessageId.Should().Be(second.Value.MessageId, "the same AgentInteractionId must never produce a second Message");
        (await CountMessagesAsync(tenantId, conversationId)).Should().Be(1);
    }

    [Fact]
    public async Task Send_fails_when_the_Conversation_does_not_exist()
    {
        var tenantId = Guid.NewGuid();
        var guestContact = new ReservationGuestContact(Guid.NewGuid(), "+5511999998888", "Ana Silva");
        using var host = BuildHost(guestContact);

        var result = await SendAsync(host, tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Olá");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConversationNotFound");
    }

    [Fact]
    public async Task Send_fails_when_the_guest_contact_has_no_phone()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        using var host = BuildHost(guestContact: null);

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Olá");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GuestContactOrPhoneNotAvailable");
    }

    [Fact]
    public async Task Send_marks_the_Message_Failed_when_the_connector_rejects_and_returns_a_failure_result()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        var guestContact = new ReservationGuestContact(reservationId, "+5511999998888", "Ana Silva");
        using var host = BuildHost(guestContact, FakeOutboundMessageConnector.Rejecting("provider_unavailable"));

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Olá");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("provider_unavailable");
    }

    /// <summary>
    /// CP5.3E corrective fix: reproduces the real Homolog/production
    /// composition — <see cref="AddCommunicationModule"/> called with
    /// <c>isDevelopmentEnvironment: false</c> and NO connector override —
    /// the exact shape that previously threw
    /// <c>InvalidOperationException: Unable to resolve service for type
    /// IOutboundMessageConnector</c> instead of resolving and failing
    /// explicitly.
    /// </summary>
    [Fact]
    public async Task Send_resolves_via_DI_and_fails_explicitly_when_no_real_connector_is_configured_for_a_non_Development_environment()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        var guestContact = new ReservationGuestContact(reservationId, "+5511999998888", "Ana Silva");
        using var host = BuildHostWithoutConnectorOverride(guestContact);

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Olá");

        result.IsFailure.Should().BeTrue("DI resolution must succeed and the connector must fail explicitly, never throw");
        result.Error.Code.Should().Be("outbound_channel_not_configured");

        (await CountMessagesAsync(tenantId, conversationId)).Should().Be(1, "the Message row is still created before the connector call");
        var message = await ReadOnlyMessageForConversationAsync(tenantId, conversationId);
        message.Status.Should().Be(MessageStatus.Failed, "a failed dispatch must never be mistaken for Sent");
    }

    // ---- Composition root -------------------------------------------------

    /// <summary>Mirrors <see cref="BuildHost"/> but never overrides <see cref="IOutboundMessageConnector"/> and passes <c>isDevelopmentEnvironment: false</c> — the real non-Development composition.</summary>
    private IHost BuildHostWithoutConnectorOverride(ReservationGuestContact? guestContact)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddCommunicationModule(configuration, isDevelopmentEnvironment: false);
        hostBuilder.Services.AddScoped<IReservationGuestContactReader>(_ => FakeReservationGuestContactReader.Returning(guestContact));

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, MessagingSchema, typeof(CommunicationDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    private IHost BuildHost(ReservationGuestContact? guestContact, IOutboundMessageConnector? connectorOverride = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddCommunicationModule(configuration);
        hostBuilder.Services.AddScoped<IReservationGuestContactReader>(_ => FakeReservationGuestContactReader.Returning(guestContact));
        hostBuilder.Services.AddScoped(_ => connectorOverride ?? FakeOutboundMessageConnector.Succeeding());

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, MessagingSchema, typeof(CommunicationDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    private static async Task<Result<SendAgentResponseResult>> SendAsync(
        IHost host, Guid tenantId, Guid conversationId, Guid reservationId, Guid agentInteractionId, string content)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommunicationRequestDispatcher>();

        return await dispatcher.Send(new SendAgentResponseCommand
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            ReservationId = reservationId,
            AgentInteractionId = agentInteractionId,
            Content = content,
        });
    }

    private async Task<Guid> SeedConversationAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var conversation = Conversation.Create(Guid.NewGuid(), tenantId, reservationId, Channel, DateTimeOffset.UtcNow);
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return conversation.Id;
    }

    private async Task<Message?> ReadMessageAsync(Guid tenantId, Guid messageId)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == messageId);
    }

    private async Task<int> CountMessagesAsync(Guid tenantId, Guid conversationId)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.Messages.CountAsync(m => m.ConversationId == conversationId);
    }

    private async Task<Message> ReadOnlyMessageForConversationAsync(Guid tenantId, Guid conversationId)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        return await dbContext.Messages.AsNoTracking().SingleAsync(m => m.ConversationId == conversationId);
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
