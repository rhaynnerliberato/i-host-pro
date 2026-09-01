using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit) — drives
/// <see cref="SendHumanHandoffNotificationCommand"/> through the real
/// <see cref="ICommunicationRequestDispatcher"/> against a real PostgreSQL
/// instance. Mirrors <c>SendAgentResponseCommandHandlerTests</c> exactly,
/// with a real <see cref="AdministratorNotificationContact"/> row instead of
/// a faked <c>IReservationGuestContactReader</c>.
/// </summary>
public class SendHumanHandoffNotificationCommandHandlerTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private const string MessagingSchema = "communication_messaging";
    private const string Channel = "WhatsApp";

    private readonly CommunicationMessageExecutionScopeTests.Fixture _fixture;

    public SendHumanHandoffNotificationCommandHandlerTests(CommunicationMessageExecutionScopeTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Send_creates_a_real_outbound_Message_to_the_administrator_contact()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        await SeedAdministratorContactAsync(tenantId, "+5511977776666");
        using var host = BuildHost();

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Refund");

        result.IsFailure.Should().BeFalse();
        var message = await ReadMessageAsync(tenantId, result.Value.MessageId);
        message.Should().NotBeNull();
        message!.Status.Should().Be(MessageStatus.Sent);
        message.TemplateKey.Should().Be("AI_HUMAN_HANDOFF_NOTIFICATION");
        message.RenderedContent.Should().Contain("Refund").And.Contain(reservationId.ToString());
    }

    [Fact]
    public async Task Send_masks_the_administrator_phone_never_persisting_it_in_full()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        await SeedAdministratorContactAsync(tenantId, "+5511977776666");
        using var host = BuildHost();

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Accident");

        var message = await ReadMessageAsync(tenantId, result.Value.MessageId);
        message!.DestinationMasked.Should().NotContain("+5511977776666");
        message.DestinationMasked.Should().EndWith("6666");
    }

    [Fact]
    public async Task Send_is_idempotent_for_the_same_AgentHumanHandoffId_returning_the_same_MessageId()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        await SeedAdministratorContactAsync(tenantId, "+5511977776666");
        var handoffId = Guid.NewGuid();
        using var host = BuildHost();

        var first = await SendAsync(host, tenantId, conversationId, reservationId, handoffId, "Police");
        var second = await SendAsync(host, tenantId, conversationId, reservationId, handoffId, "Police");

        first.Value.MessageId.Should().Be(second.Value.MessageId, "the same AgentHumanHandoffId must never produce a second Message");
        (await CountMessagesAsync(tenantId, conversationId)).Should().Be(1);
    }

    [Fact]
    public async Task Send_fails_when_the_Conversation_does_not_exist()
    {
        var tenantId = Guid.NewGuid();
        await SeedAdministratorContactAsync(tenantId, "+5511977776666");
        using var host = BuildHost();

        var result = await SendAsync(host, tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Refund");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ConversationNotFound");
    }

    [Fact]
    public async Task Send_fails_when_no_active_administrator_contact_exists()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        using var host = BuildHost();

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "Refund");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NoActiveAdministratorNotificationContact");
    }

    [Fact]
    public async Task Send_never_includes_credential_or_QR_like_content()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var conversationId = await SeedConversationAsync(tenantId, reservationId);
        await SeedAdministratorContactAsync(tenantId, "+5511977776666");
        using var host = BuildHost();

        var result = await SendAsync(host, tenantId, conversationId, reservationId, Guid.NewGuid(), "SevereDamage");

        var message = await ReadMessageAsync(tenantId, result.Value.MessageId);
        message!.RenderedContent.Should().NotContainAny("QrCodePayload", "AccessCredential", "senha", "+5511977776666");
    }

    // ---- Composition root -------------------------------------------------

    private IHost BuildHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddCommunicationModule(configuration);
        hostBuilder.Services.AddScoped<IOutboundMessageConnector>(_ => FakeOutboundMessageConnector.Succeeding());

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, MessagingSchema, typeof(CommunicationDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    private static async Task<Result<SendHumanHandoffNotificationResult>> SendAsync(
        IHost host, Guid tenantId, Guid conversationId, Guid reservationId, Guid agentHumanHandoffId, string reasonCode)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommunicationRequestDispatcher>();

        return await dispatcher.Send(new SendHumanHandoffNotificationCommand
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            ReservationId = reservationId,
            AgentHumanHandoffId = agentHumanHandoffId,
            ReasonCode = reasonCode,
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

    private async Task SeedAdministratorContactAsync(Guid tenantId, string phone)
    {
        var tenantContext = NewTenantScopedContext(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), tenantId, phone, DateTimeOffset.UtcNow);
        dbContext.AdministratorNotificationContacts.Add(contact);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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
