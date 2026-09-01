using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 5 (Policies, Workflow &amp; Conversational
/// Orchestration) — mandatory real transport E2E gate. Reuses
/// <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// verbatim (own container instance, same shape as
/// <c>AIAgentReadToolsWorkflowRoundTripTests</c>/<c>AIAgentWriteToolsWorkflowRoundTripTests</c>).
///
/// What these scenarios prove, end-to-end through the real Api → outbox →
/// RabbitMQ → Worker → AI Agent consumer pipeline: the one-controlled-retry
/// policy genuinely recovers a transient model failure; an unknown
/// (non-allowlisted) ToolName is never dispatched yet still answered safely;
/// unsupported-request/human-handoff-requested intents are classified via
/// <c>ModelResult.Intent</c> without ever claiming an action that did not
/// happen; and a Write Tool's own offset-less-datetime rejection (Checkpoint
/// 5's timezone-safety fix) survives the real orchestration path, not just a
/// unit test.
///
/// Model-retry scenarios that need a REAL Tool's own execution to interleave
/// with a controlled model failure at Call#2 specifically (post-write model
/// failure — mandate item 29/50) are NOT reproducible here with real,
/// production Tools, since their own success content is fixed text, not
/// test-marker-controllable — that scenario is covered instead by a
/// dedicated Unit test
/// (<c>ConversationMessageReceivedProcessorTests.HandleAsync_a_permanent_Call2_model_failure_falls_back_to_the_known_tool_content_verbatim_and_the_interaction_succeeds</c>),
/// using a fake Tool whose own content can legitimately embed the
/// <see cref="FakeModelProvider.FailureTriggerMarker"/>. Likewise,
/// <see cref="AgentResponseDeliveryService"/>'s own response-delivery retry
/// (mandate item 32) is covered by a dedicated Unit test against a scripted
/// dispatcher, since the real dev <c>IOutboundMessageConnector</c> has no
/// deterministic "fail once" seam.
/// </summary>
public sealed class AIAgentConversationalOrchestrationWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-aiagent-phone-number-id";
    private const string AppSecret = "e2e-aiagent-test-app-secret";

    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public AIAgentConversationalOrchestrationWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    private static readonly Guid GlobalTenantId = ConversationMessageReceivedWorkflowRoundTripTests.GlobalTenantId;

    [Fact]
    public async Task Transient_model_failure_on_Call1_is_retried_once_and_the_read_only_interaction_still_succeeds()
    {
        const string guestPhone = "+5511830000001";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.CP5-TRANSIENT-RETRY", "5511830000001",
            $"como está minha reserva? {FakeModelProvider.TransientFailureTriggerMarker} {FakeModelProvider.ToolCallTriggerPrefix}GetReservationSummary]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP5-TRANSIENT-RETRY");
        var interaction = await WaitForInteractionAsync(message!.Id);

        interaction!.Outcome.Should().Be(
            AgentInteractionOutcome.Success, "attempt #1 fails but the one controlled retry (mandate item 26) recovers it — " + WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("GetReservationSummary");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success);

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        withOutboundMessage.Should().NotBeNull(WorkerSnapshot());
    }

    [Fact]
    public async Task Unknown_tool_name_is_never_dispatched_but_the_interaction_still_answers_safely()
    {
        const string guestPhone = "+5511830000002";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.CP5-UNKNOWN-TOOL", "5511830000002", $"faça algo estranho {FakeModelProvider.ToolCallTriggerPrefix}ThisToolDoesNotExist]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP5-UNKNOWN-TOOL");
        var interaction = await WaitForInteractionAsync(message!.Id);

        interaction!.Outcome.Should().Be(
            AgentInteractionOutcome.Success, "Checkpoint 5 — an unknown ToolName never fails the whole interaction, unlike a real tool's own failure — " + WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle("the unknown tool name is still audited, even though it never dispatches");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Failure);
        toolExecutions[0].FailureCode.Should().Be("unknown_tool");

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        withOutboundMessage.Should().NotBeNull("a safe response is still delivered — " + WorkerSnapshot());
    }

    [Fact]
    public async Task Unsupported_request_intent_is_classified_and_answered_safely_without_calling_any_tool()
    {
        const string guestPhone = "+5511830000003";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.CP5-UNSUPPORTED", "5511830000003", $"quero cancelar minha reserva {FakeModelProvider.UnsupportedRequestTriggerMarker}");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP5-UNSUPPORTED");
        var interaction = await WaitForInteractionAsync(message!.Id);

        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        interaction.Intent.Should().Be("unsupported_request");

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().BeEmpty("no Command/Tool is ever called for an unsupported request — CancelReservation remains FORBIDDEN");

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        withOutboundMessage.Should().NotBeNull(WorkerSnapshot());
    }

    /// <summary>
    /// Updated for Fase 11, Checkpoint 6: <c>human_handoff_requested</c> now
    /// drives a REAL <see cref="AgentHumanHandoff"/>/<see cref="AgentSession"/>
    /// escalation (superseding this test's original Checkpoint 5 premise,
    /// which predates the classifier being wired to
    /// <c>ProcessHumanHandoffRequestAsync</c>) — no <c>AdministratorNotificationContact</c>
    /// is seeded in this class, so the notification attempt genuinely fails
    /// and the handoff stays <see cref="AgentHumanHandoffStatus.Requested"/>;
    /// the guest ack must still never overclaim a notification that did not
    /// happen. The real "notification succeeds" path is covered by
    /// <c>AIAgentHumanHandoffWorkflowRoundTripTests</c> (Checkpoint 6's own
    /// dedicated E2E gate).
    /// </summary>
    [Fact]
    public async Task Human_handoff_requested_intent_creates_a_real_handoff_and_the_ack_never_overclaims_notification_success()
    {
        const string guestPhone = "+5511830000004";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.CP5-HANDOFF", "5511830000004", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP5-HANDOFF");
        var interaction = await WaitForInteractionAsync(message!.Id);

        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        interaction.Intent.Should().Be("human_handoff_requested");

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().BeEmpty("a restricted intent is never dispatched as a Tool call — it preempts tool/confirmation handling entirely");

        var session = await ReadSessionByIdAsync(interaction.AgentSessionId);
        session!.Status.Should().Be(AgentSessionStatus.Escalated, "Checkpoint 6 — an explicit human request genuinely suspends the AI, not just classifies it");

        var handoff = await ReadHandoffByAgentSessionIdAsync(interaction.AgentSessionId);
        handoff.Should().NotBeNull("a real AgentHumanHandoff row must exist now that the classifier is wired to ProcessHumanHandoffRequestAsync");
        handoff!.ReasonCode.Should().Be(AgentHumanHandoffReasonCode.ExplicitHumanRequest);
        handoff.Status.Should().Be(AgentHumanHandoffStatus.Requested, "no AdministratorNotificationContact is seeded in this test class — notification must genuinely fail, never be reported as Notified");

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        withOutboundMessage.Should().NotBeNull(WorkerSnapshot());

        var outboundMessage = await ReadMessageByIdAsync(withOutboundMessage!.Value);
        outboundMessage!.RenderedContent.Should().NotContain("já encaminhei", "the response must never claim a notification that did not actually happen")
            .And.NotContain("foi notificado");
    }

    [Fact]
    public async Task Early_Check_In_proposal_with_an_offset_less_datetime_is_rejected_never_creates_a_pending_action()
    {
        const string guestPhone = "+5511830000005";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        var offsetLessRequestedCheckInAt = DateTimeOffset.UtcNow.AddDays(29).ToString("yyyy-MM-ddTHH:mm:ss");

        var response = await SendInboundMessageAsync(
            "wamid.CP5-NO-OFFSET", "5511830000005",
            $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={offsetLessRequestedCheckInAt}]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP5-NO-OFFSET");
        var interaction = await WaitForInteractionAsync(message!.Id);

        interaction!.Outcome.Should().Be(
            AgentInteractionOutcome.Failure,
            "Checkpoint 5 (mandate item 20) — an offset-less datetime must be rejected, never silently interpreted using the server's local timezone — " + WorkerSnapshot());

        (await CountActivePendingActionsAsync(reservationId)).Should().Be(0, "a rejected proposal must never create a pending action");
    }

    // ---- Helpers ----------------------------------------------------------

    private string WorkerSnapshot() => "Worker output:\n" + _fixture.GetWorkerOutputSnapshot();

    private async Task<HttpResponseMessage> SendInboundMessageAsync(string providerMessageId, string from, string? text)
    {
        var textPart = text is null ? "" : ",\"text\":{\"body\":\"" + text.Replace("\"", "\\\"") + "\"}";
        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + PhoneNumberId + "\"}," +
            "\"messages\":[{\"id\":\"" + providerMessageId + "\",\"from\":\"" + from + "\",\"type\":\"text\"," +
            "\"timestamp\":\"" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\"" + textPart + "}]" +
            "}}]}]}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", ComputeSignature(body, AppSecret));
        return await _fixture.ApiClient.SendAsync(request);
    }

    private static string ComputeSignature(string body, string appSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<Message?> WaitForInboundMessageAsync(string providerMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageByProviderMessageIdAsync(providerMessageId);
            if (message is not null)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadMessageByProviderMessageIdAsync(providerMessageId);
    }

    private async Task<Message?> ReadMessageByProviderMessageIdAsync(string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == GlobalTenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<Message?> ReadMessageByIdAsync(Guid messageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var message = await dbContext.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<AgentInteraction?> WaitForInteractionAsync(Guid inboundMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var interaction = await ReadInteractionAsync(inboundMessageId);
            if (interaction is not null && interaction.Outcome != AgentInteractionOutcome.InProgress)
                return interaction;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadInteractionAsync(inboundMessageId);
    }

    private async Task<Guid?> WaitForOutboundMessageIdAsync(Guid inboundMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var interaction = await ReadInteractionAsync(inboundMessageId);
            if (interaction?.OutboundMessageId is not null)
                return interaction.OutboundMessageId;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return (await ReadInteractionAsync(inboundMessageId))?.OutboundMessageId;
    }

    private async Task<AgentInteraction?> ReadInteractionAsync(Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var interaction = await dbContext.AgentInteractions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TenantId == GlobalTenantId && i.InboundMessageId == inboundMessageId);

        await transaction.CommitAsync();
        return interaction;
    }

    private async Task<AgentSession?> ReadSessionByIdAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var session = await dbContext.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == agentSessionId);

        await transaction.CommitAsync();
        return session;
    }

    private async Task<AgentHumanHandoff?> ReadHandoffByAgentSessionIdAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var handoff = await dbContext.AgentHumanHandoffs.AsNoTracking().FirstOrDefaultAsync(h => h.AgentSessionId == agentSessionId);

        await transaction.CommitAsync();
        return handoff;
    }

    private async Task<List<AgentToolExecution>> ReadToolExecutionsAsync(Guid agentInteractionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var executions = await dbContext.AgentToolExecutions.AsNoTracking()
            .Where(e => e.TenantId == GlobalTenantId && e.AgentInteractionId == agentInteractionId)
            .ToListAsync();

        await transaction.CommitAsync();
        return executions;
    }

    // Scoped by the test's own ReservationId (via its AgentSession), never
    // "most recent for the tenant" — every scenario in this class shares one
    // GlobalTenantId (CP4's own established fix for cross-test contamination).
    private async Task<int> CountActivePendingActionsAsync(Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var sessionId = await dbContext.AgentSessions.AsNoTracking()
            .Where(s => s.TenantId == GlobalTenantId && s.ReservationId == reservationId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync();

        var count = sessionId is null ? 0 : await dbContext.AgentPendingActions.AsNoTracking()
            .CountAsync(a => a.AgentSessionId == sessionId
                && (a.Status == AgentPendingActionStatus.Proposed || a.Status == AgentPendingActionStatus.Confirmed));

        await transaction.CommitAsync();
        return count;
    }

    private static async Task SetTenantAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private CommunicationDbContext CreateCommunicationDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }

    private AIAgentDbContext CreateAIAgentDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<AIAgentDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent"))
            .Options;
        return new AIAgentDbContext(options, tenantContext);
    }

    // ---- Domain seeding -----------------------------------------------------

    private async Task<(Guid ReservationId, Guid PropertyId)> SeedConfirmedReservationAsync(
        string guestPhone, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var propertyId = await SeedActivePropertyAsync();
        var reservationId = await SeedConfirmedReservationOnPropertyAsync(propertyId, guestPhone, checkInAt, checkOutAt);
        return (reservationId, propertyId);
    }

    private async Task<Guid> SeedConfirmedReservationOnPropertyAsync(
        Guid propertyId, string guestPhone, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(GlobalTenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

        var result = await dispatcher.Send(new CreateReservationCommand(
            GlobalTenantId, Guid.NewGuid(), propertyId, "Test Guest", guestPhone, checkInAt, checkOutAt, GuestCount: 2));
        result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
        return result.Value.Id;
    }

    private async Task<Guid> SeedActivePropertyAsync()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var propertyDbContext = new IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext(
            new DbContextOptionsBuilder<IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext>()
                .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
                .Options,
            tenantContext);
        await using var transaction = await propertyDbContext.Database.BeginTransactionAsync();
        await propertyDbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {GlobalTenantId.ToString()}, true)");

        var now = DateTimeOffset.UtcNow;
        var address = IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var property = IHostPro.Contexts.PropertyManagement.Domain.Property.Create(
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"CP5-{Guid.NewGuid():N}"[..12]),
            "Test Property", capacity: 4, condominiumId: null, address, now);
        property.Activate(now);

        propertyDbContext.Properties.Add(property);
        await propertyDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Housekeeping's own property-eligibility projection must exist for Reservations to accept a new booking.
        await using var connection = new Npgsql.NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var projectionTransaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{GlobalTenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText =
                "INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active) VALUES (@tenantId, @propertyId, true)";
            insertCommand.Parameters.AddWithValue("tenantId", GlobalTenantId);
            insertCommand.Parameters.AddWithValue("propertyId", property.Id);
            await insertCommand.ExecuteNonQueryAsync();
        }
        await projectionTransaction.CommitAsync();

        return property.Id;
    }
}
