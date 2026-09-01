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
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 4 (Write Tools & Response Delivery) — mandatory real
/// transport E2E gate. Reuses <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// verbatim (own container instance, same shape as
/// <c>AIAgentReadToolsWorkflowRoundTripTests</c>): a signed WhatsApp inbound
/// webhook flows through the real Api → real outbox → real RabbitMQ → real
/// Worker subprocess → AI Agent's own consumer → the real
/// <see cref="FakeModelProvider"/> confirmation loop → a real Guest
/// Operations write Command (only after confirmation) → a real Communication
/// outbound <c>Message</c> (<see cref="SendAgentResponseCommand"/>).
///
/// What these scenarios prove: the confirmation gate genuinely blocks
/// execution until confirmed; the real business Command runs exactly once,
/// only after confirmation; a business denial is a successful Tool
/// execution; the immediate-execution Tool never creates a pending action;
/// every interaction — read-only or write — ends in a real delivered
/// response. <see cref="FakeModelProvider"/> makes zero network calls of its
/// own, so <c>ExternalLLMNetworkCalls=0</c> is a structural fact of every
/// scenario here, not something re-probed per test.
/// </summary>
public sealed class AIAgentWriteToolsWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-aiagent-phone-number-id";
    private const string AppSecret = "e2e-aiagent-test-app-secret";

    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public AIAgentWriteToolsWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    private static readonly Guid GlobalTenantId = ConversationMessageReceivedWorkflowRoundTripTests.GlobalTenantId;

    [Fact]
    public async Task Early_Check_In_two_turn_confirmation_flow_executes_the_real_Command_exactly_once()
    {
        const string guestPhone = "+5511820000001";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        var requestedCheckInAt = DateTimeOffset.UtcNow.AddDays(29).ToString("O");

        // ---- Turn 1: propose ----
        var proposeResponse = await SendInboundMessageAsync(
            "wamid.CP4-EARLY-PROPOSE", "5511820000001",
            $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={requestedCheckInAt}]");
        proposeResponse.EnsureSuccessStatusCode();

        var proposeMessage = await WaitForInboundMessageAsync("wamid.CP4-EARLY-PROPOSE");
        var proposeInteraction = await WaitForInteractionAsync(proposeMessage!.Id);
        proposeInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var pendingAction = await WaitForActivePendingActionAsync(reservationId);
        pendingAction.Should().NotBeNull(WorkerSnapshot());
        pendingAction!.ToolName.Should().Be("RequestEarlyCheckIn");
        pendingAction.Status.Should().Be(AgentPendingActionStatus.Proposed);

        (await CountEarlyCheckInRequestsAsync(reservationId)).Should().Be(0, "the real Command must never run before confirmation");

        // ---- Turn 2: confirm ----
        var confirmResponse = await SendInboundMessageAsync(
            "wamid.CP4-EARLY-CONFIRM", "5511820000001", $"sim, confirmo {FakeModelProvider.ConfirmTriggerMarker}");
        confirmResponse.EnsureSuccessStatusCode();

        var confirmMessage = await WaitForInboundMessageAsync("wamid.CP4-EARLY-CONFIRM");
        var confirmInteraction = await WaitForInteractionAsync(confirmMessage!.Id);
        confirmInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var executedPendingAction = await ReadPendingActionByIdAsync(pendingAction.Id);
        executedPendingAction!.Status.Should().Be(AgentPendingActionStatus.Executed, WorkerSnapshot());

        (await CountEarlyCheckInRequestsAsync(reservationId)).Should().Be(1, "the real Command must run exactly once, only after confirmation");
    }

    [Fact]
    public async Task Early_Check_In_business_denial_is_a_successful_tool_execution_never_a_technical_failure()
    {
        const string guestPhone = "+5511820000002";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        var requestedCheckInAt = DateTimeOffset.UtcNow.AddDays(29).ToString("O");
        // Deliberately no EARLY_CHECKIN policy seeded — the real Command must deny with PolicyNotConfigured.

        var proposeResponse = await SendInboundMessageAsync(
            "wamid.CP4-EARLY-DENY-PROPOSE", "5511820000002",
            $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={requestedCheckInAt}]");
        proposeResponse.EnsureSuccessStatusCode();
        var proposeMessage = await WaitForInboundMessageAsync("wamid.CP4-EARLY-DENY-PROPOSE");
        await WaitForInteractionAsync(proposeMessage!.Id);
        (await WaitForActivePendingActionAsync(reservationId)).Should().NotBeNull(WorkerSnapshot());

        var confirmResponse = await SendInboundMessageAsync(
            "wamid.CP4-EARLY-DENY-CONFIRM", "5511820000002", $"sim {FakeModelProvider.ConfirmTriggerMarker}");
        confirmResponse.EnsureSuccessStatusCode();
        var confirmMessage = await WaitForInboundMessageAsync("wamid.CP4-EARLY-DENY-CONFIRM");
        var confirmInteraction = await WaitForInteractionAsync(confirmMessage!.Id);

        confirmInteraction!.Outcome.Should().Be(
            AgentInteractionOutcome.Success, "a real business denial (PolicyNotConfigured) is still a successful Tool execution — " + WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(confirmInteraction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success);

        (await CountEarlyCheckInRequestsAsync(reservationId)).Should().Be(1, "the real Command still runs and persists a Denied request row");
    }

    [Fact]
    public async Task Access_Delivery_executes_immediately_with_no_pending_action()
    {
        const string guestPhone = "+5511820000003";
        var (reservationId, propertyId) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        await SeedPropertyAccessConfigurationAsync(propertyId, "Use o código 4321 no portão.");

        var response = await SendInboundMessageAsync(
            "wamid.CP4-ACCESS-DELIVERY", "5511820000003", $"me envie a senha {FakeModelProvider.ToolCallTriggerPrefix}RequestGuestAccessDelivery]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP4-ACCESS-DELIVERY");
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("RequestGuestAccessDelivery");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());

        (await CountActivePendingActionsAsync(reservationId)).Should().Be(
            0, "EXPLICIT_REQUEST_IS_CONFIRMATION — RequestGuestAccessDelivery never creates a pending action");
    }

    [Fact]
    public async Task Response_only_read_interaction_delivers_a_real_outbound_Message()
    {
        const string guestPhone = "+5511820000004";
        var (_, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.CP4-RESPONSE-ONLY", "5511820000004", $"como está minha reserva? {FakeModelProvider.ToolCallTriggerPrefix}GetReservationSummary]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP4-RESPONSE-ONLY");
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        withOutboundMessage.Should().NotBeNull("CP4 — every successful interaction delivers a real response. " + WorkerSnapshot());

        var outboundMessage = await ReadMessageByIdAsync(withOutboundMessage!.Value);
        outboundMessage.Should().NotBeNull();
        outboundMessage!.Direction.Should().Be(MessageDirection.Outbound);
        outboundMessage.ConversationId.Should().Be(message.ConversationId);
        outboundMessage.TemplateKey.Should().Be("AI_AGENT_RESPONSE");
        outboundMessage.RenderedContent.Should().NotBeNullOrWhiteSpace();
        outboundMessage.DestinationMasked.Should().EndWith("0004");
    }

    [Fact]
    public async Task Duplicate_confirmation_delivered_twice_executes_the_real_Command_exactly_once()
    {
        const string guestPhone = "+5511820000005";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        var requestedCheckInAt = DateTimeOffset.UtcNow.AddDays(29).ToString("O");

        await SendInboundMessageAsync(
            "wamid.CP4-DUP-PROPOSE", "5511820000005",
            $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={requestedCheckInAt}]");
        var proposeMessage = await WaitForInboundMessageAsync("wamid.CP4-DUP-PROPOSE");
        await WaitForInteractionAsync(proposeMessage!.Id);
        (await WaitForActivePendingActionAsync(reservationId)).Should().NotBeNull(WorkerSnapshot());

        var firstConfirmResponse = await SendInboundMessageAsync(
            "wamid.CP4-DUP-CONFIRM", "5511820000005", $"sim {FakeModelProvider.ConfirmTriggerMarker}");
        firstConfirmResponse.EnsureSuccessStatusCode();
        var confirmMessage = await WaitForInboundMessageAsync("wamid.CP4-DUP-CONFIRM");
        await WaitForInteractionAsync(confirmMessage!.Id);

        // Redelivered only AFTER the first delivery's own processing (pending
        // action Confirmed -> Executed, real Command run) has already fully
        // completed — the realistic redelivery scenario the CP4 mandate
        // itself describes, never a raw concurrent-delivery race (already
        // covered generically by the CP3 idempotency E2E).
        var secondConfirmResponse = await SendInboundMessageAsync(
            "wamid.CP4-DUP-CONFIRM", "5511820000005", $"sim {FakeModelProvider.ConfirmTriggerMarker}");
        secondConfirmResponse.EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountInteractionsAsync(confirmMessage!.Id)).Should().Be(1, "a redelivered confirmation must never produce a second AgentInteraction");
        (await CountEarlyCheckInRequestsAsync(reservationId)).Should().Be(1, "a redelivered confirmation must never re-execute the real Command");
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

    private async Task<int> CountInteractionsAsync(Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.AgentInteractions.AsNoTracking()
            .CountAsync(i => i.TenantId == GlobalTenantId && i.InboundMessageId == inboundMessageId);

        await transaction.CommitAsync();
        return count;
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
    // GlobalTenantId, so a tenant-wide "most recent" query could observe a
    // DIFFERENT test's own pending action.
    private async Task<AgentPendingAction?> WaitForActivePendingActionAsync(Guid reservationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var pendingAction = await ReadActivePendingActionAsync(reservationId);
            if (pendingAction is not null)
                return pendingAction;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadActivePendingActionAsync(reservationId);
    }

    private async Task<AgentPendingAction?> ReadActivePendingActionAsync(Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var pendingAction = await (
            from a in dbContext.AgentPendingActions.AsNoTracking()
            join s in dbContext.AgentSessions.AsNoTracking() on a.AgentSessionId equals s.Id
            where s.TenantId == GlobalTenantId && s.ReservationId == reservationId
                && (a.Status == AgentPendingActionStatus.Proposed || a.Status == AgentPendingActionStatus.Confirmed)
            select a).FirstOrDefaultAsync();

        await transaction.CommitAsync();
        return pendingAction;
    }

    private async Task<AgentPendingAction?> ReadPendingActionByIdAsync(Guid id)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var pendingAction = await dbContext.AgentPendingActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);

        await transaction.CommitAsync();
        return pendingAction;
    }

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

    private async Task<int> CountEarlyCheckInRequestsAsync(Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateGuestOperationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.EarlyCheckInRequests.AsNoTracking()
            .CountAsync(r => r.TenantId == GlobalTenantId && r.ReservationId == reservationId);

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

    private GuestOperationsDbContext CreateGuestOperationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;
        return new GuestOperationsDbContext(options, tenantContext);
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
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"CP4-{Guid.NewGuid():N}"[..12]),
            "Test Property", capacity: 4, condominiumId: null, address, now);
        property.Activate(now);

        propertyDbContext.Properties.Add(property);
        await propertyDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Housekeeping's own property-eligibility projection must exist for Reservations to accept a new booking.
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
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

    private async Task SeedPropertyAccessConfigurationAsync(Guid propertyId, string accessInstructions)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(GlobalTenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

        var result = await dispatcher.Send(new SetPropertyAccessConfigurationCommand(
            GlobalTenantId, Guid.NewGuid(), propertyId, AccessCredentialSecretReference: "vault://e2e-not-real", accessInstructions, IsActive: true));
        result.IsSuccess.Should().BeTrue("seeding the property access configuration must succeed — this is a real Application write, not a mock");
    }
}
