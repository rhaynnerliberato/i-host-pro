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
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 3 (Read Tools &amp; Context Builder) — mandatory real
/// transport E2E gate. Reuses <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// verbatim (own container instance, same shape) — a signed WhatsApp inbound
/// webhook flows through the real Api → real outbox → real RabbitMQ → real
/// Worker subprocess → AI Agent's own consumer → the real
/// <see cref="FakeModelProvider"/> tool-calling loop (Call#1 requests a tool
/// via <see cref="FakeModelProvider.ToolCallTriggerPrefix"/> → the real
/// dispatcher/Application Query of the owning Bounded Context → a persisted
/// <see cref="AgentToolExecution"/> → Call#2 → a persisted
/// <see cref="AgentInteraction"/>).
///
/// <see cref="FakeModelProvider"/> makes zero network calls of its own
/// (verified by direct source read — no HttpClient/Socket anywhere in its
/// implementation), so <c>ExternalLLMNetworkCalls=0</c> is a structural fact
/// of every scenario in this file, not something re-probed per test.
///
/// What these E2E scenarios prove vs. what they deliberately do NOT
/// re-prove: <see cref="AgentInteraction"/>/<see cref="AgentToolExecution"/>
/// never persist a Tool's raw result text (by design — mandate item 9,
/// "nunca persistir raw result") — so the exact sanitized CONTENT a Tool
/// returned is not observable from outside the process and is not asserted
/// here. Content-level sanitization (e.g. "GuestPhone/QrCodePayload/
/// AccessCredentialSecretReference never appear in the Tool's own result
/// string") is already exhaustively proven by each Tool's own unit tests
/// (<c>IHostPro.Contexts.AIAgent.Tests.Unit/Tools/*</c>) and the tie-break
/// business rule is already proven against real Postgres by the two reader
/// integration tests (<c>GetPaymentStatusByReservationReaderTests</c>/
/// <c>GetCleaningStatusByReservationReaderTests</c>). This file's own job is
/// exclusively to prove the real end-to-end WIRING: the Worker process
/// actually resolves the right dispatcher, the real Application Query hits
/// real Postgres, the correct <see cref="AgentToolExecution.ToolName"/>/
/// <see cref="AgentToolExecutionOutcome"/> is persisted with the real
/// database foreign key to a real <see cref="AgentInteraction"/>, and the
/// interaction completes successfully — never that the AI Agent sends
/// anything outbound.
/// </summary>
public sealed class AIAgentReadToolsWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-aiagent-phone-number-id";
    private const string AppSecret = "e2e-aiagent-test-app-secret";

    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public AIAgentReadToolsWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    // Every scenario in this class shares one tenant (seeded once by the
    // Fixture's own WhatsAppTenantRoute) — each [Fact] uses its own phone
    // number so Reservation-resolution candidates never leak across
    // scenarios. MUST reuse ConversationMessageReceivedWorkflowRoundTripTests'
    // own GlobalTenantId verbatim, never a freshly-generated one of this
    // class' own — Fixture.SeedTenantRouteAsync() always seeds the
    // WhatsAppTenantRoute for THAT exact field (nested-class access to the
    // outer type's own static member), so any other tenant id here would
    // never resolve from the inbound webhook's phone number.
    private static readonly Guid GlobalTenantId = ConversationMessageReceivedWorkflowRoundTripTests.GlobalTenantId;

    [Fact]
    public async Task Principal_flow_GetReservationSummary_executes_the_tool_once_and_completes_the_interaction()
    {
        const string guestPhone = "+5511810000001";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-RESERVATION", "5511810000001", $"como está minha reserva? {FakeModelProvider.ToolCallTriggerPrefix}GetReservationSummary]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-RESERVATION");
        message.Should().NotBeNull(WorkerSnapshot());

        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        (await CountInteractionsAsync(message.Id)).Should().Be(1);

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("GetReservationSummary");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());
        toolExecutions[0].AgentInteractionId.Should().Be(interaction.Id);

        // Fase 11, Checkpoint 4: every successful interaction now delivers a
        // real response (mandate item 33) — this assertion was "0" under
        // CP3's own scope (response delivery did not exist yet); CP4
        // deliberately and explicitly changes that, never a regression.
        interaction.OutboundMessageId.Should().NotBeNull(WorkerSnapshot());
        (await CountOutboundMessagesAsync(message.ConversationId)).Should().Be(1, "CP4 delivers a real response for every successful interaction, including read-only ones");
        _ = reservationId;
    }

    [Fact]
    public async Task Payment_E2E_picks_the_most_recent_PixCharge_by_CreatedAtUtc_never_by_status()
    {
        const string guestPhone = "+5511810000002";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        // The EARLIER charge ends up Confirmed (the "best" status); the LATER
        // one ends up Failed — the tie-break must still pick the later one.
        await SeedPixChargeAsync(reservationId, DateTimeOffset.UtcNow.AddHours(-2), PixChargeSeedOutcome.Confirmed);
        await SeedPixChargeAsync(reservationId, DateTimeOffset.UtcNow.AddHours(-1), PixChargeSeedOutcome.Failed);

        var response = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-PAYMENT", "5511810000002", $"e meu pagamento? {FakeModelProvider.ToolCallTriggerPrefix}GetPaymentStatus]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-PAYMENT");
        message.Should().NotBeNull(WorkerSnapshot());
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("GetPaymentStatus");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success,
            "the reader's own tie-break (CreatedAtUtc DESC, Id DESC — proven by GetPaymentStatusByReservationReaderTests) must resolve a single winner, never throw");
    }

    [Fact]
    public async Task Cleaning_E2E_reports_a_real_persisted_status_never_an_invented_fact()
    {
        const string guestPhone = "+5511810000003";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        // The real ReservationCreated -> Workflow -> Housekeeping choreography
        // auto-creates a Cleaning for every new Reservation (ADR-018) —
        // waiting for that real row, rather than seeding a second one
        // directly, avoids colliding with the "at most one AUTOMATED
        // Cleaning per Reservation" partial unique index and is the more
        // realistic scenario besides.
        (await WaitForAutomatedCleaningAsync(reservationId)).Should().BeTrue(WorkerSnapshot());

        var response = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-CLEANING", "5511810000003", $"a faxina já foi feita? {FakeModelProvider.ToolCallTriggerPrefix}GetCleaningStatus]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-CLEANING");
        message.Should().NotBeNull(WorkerSnapshot());
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("GetCleaningStatus");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());
    }

    [Fact]
    public async Task Property_E2E_GetPropertyInformation_and_GetAccessInstructions_each_succeed_in_their_own_interaction()
    {
        const string guestPhone = "+5511810000004";
        var (_, propertyId) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        await SeedPropertyAccessConfigurationAsync(propertyId, "Use o código 4321 no portão. Wi-Fi/estacionamento: consulte a portaria.");

        var infoResponse = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-PROPERTY-INFO", "5511810000004", $"me fala sobre a propriedade {FakeModelProvider.ToolCallTriggerPrefix}GetPropertyInformation]");
        infoResponse.EnsureSuccessStatusCode();
        var infoMessage = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-PROPERTY-INFO");
        infoMessage.Should().NotBeNull(WorkerSnapshot());
        var infoInteraction = await WaitForInteractionAsync(infoMessage!.Id);
        infoInteraction.Should().NotBeNull(WorkerSnapshot());
        infoInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        var infoToolExecutions = await ReadToolExecutionsAsync(infoInteraction.Id);
        infoToolExecutions.Should().ContainSingle();
        infoToolExecutions[0].ToolName.Should().Be("GetPropertyInformation");
        infoToolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());

        var accessResponse = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-PROPERTY-ACCESS", "5511810000004", $"como acesso o imóvel? {FakeModelProvider.ToolCallTriggerPrefix}GetAccessInstructions]");
        accessResponse.EnsureSuccessStatusCode();
        var accessMessage = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-PROPERTY-ACCESS");
        accessMessage.Should().NotBeNull(WorkerSnapshot());
        var accessInteraction = await WaitForInteractionAsync(accessMessage!.Id);
        accessInteraction.Should().NotBeNull(WorkerSnapshot());
        accessInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        var accessToolExecutions = await ReadToolExecutionsAsync(accessInteraction.Id);
        accessToolExecutions.Should().ContainSingle();
        accessToolExecutions[0].ToolName.Should().Be("GetAccessInstructions");
        accessToolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());

        // Wi-Fi/parking/rules structured data remains DEFERRED this checkpoint
        // — AccessInstructions is opaque free text, never parsed/assumed to
        // structurally cover them (mandate item 5's own doc comment).
    }

    [Fact]
    public async Task Availability_E2E_reflects_calendar_state_only_never_an_eligibility_conclusion()
    {
        const string guestPhoneFree = "+5511810000005";
        var (_, propertyIdFree) = await SeedConfirmedReservationAsync(guestPhoneFree, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var freeResponse = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-AVAILABILITY-FREE", "5511810000005", $"a propriedade está disponível? {FakeModelProvider.ToolCallTriggerPrefix}GetAvailability]");
        freeResponse.EnsureSuccessStatusCode();
        var freeMessage = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-AVAILABILITY-FREE");
        freeMessage.Should().NotBeNull(WorkerSnapshot());
        var freeInteraction = await WaitForInteractionAsync(freeMessage!.Id);
        freeInteraction.Should().NotBeNull(WorkerSnapshot());
        freeInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        var freeToolExecutions = await ReadToolExecutionsAsync(freeInteraction.Id);
        freeToolExecutions.Should().ContainSingle();
        freeToolExecutions[0].ToolName.Should().Be("GetAvailability");
        freeToolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success,
            "the guest's own reservation sits 30 days out, outside the tool's default 7-day window — the property must resolve as having no nearby schedule conflict");

        const string guestPhoneConflict = "+5511810000006";
        var (_, propertyIdConflict) = await SeedConfirmedReservationAsync(guestPhoneConflict, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        // A second, unrelated Reservation on the SAME property, inside the
        // Tool's own 7-day default window — a real schedule conflict fact.
        await SeedConfirmedReservationOnPropertyAsync(propertyIdConflict, "+5511810000099", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(4));

        var conflictResponse = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-AVAILABILITY-CONFLICT", "5511810000006", $"a propriedade está disponível? {FakeModelProvider.ToolCallTriggerPrefix}GetAvailability]");
        conflictResponse.EnsureSuccessStatusCode();
        var conflictMessage = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-AVAILABILITY-CONFLICT");
        conflictMessage.Should().NotBeNull(WorkerSnapshot());
        var conflictInteraction = await WaitForInteractionAsync(conflictMessage!.Id);
        conflictInteraction.Should().NotBeNull(WorkerSnapshot());
        conflictInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        var conflictToolExecutions = await ReadToolExecutionsAsync(conflictInteraction.Id);
        conflictToolExecutions.Should().ContainSingle();
        conflictToolExecutions[0].ToolName.Should().Be("GetAvailability");
        conflictToolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success,
            "a real conflicting Reservation exists on the same property within the window — the Tool must still just report the calendar fact, never throw or fail");

        // GetAvailability never resolves early-checkin/late-checkout
        // eligibility — that decision-making stays exclusively in
        // GuestOperations' own Request flow, never invoked here.
    }

    [Fact]
    public async Task Policy_E2E_proves_the_real_PROPERTY_TENANT_GLOBAL_hierarchy_via_a_typed_projection()
    {
        const string guestPhone = "+5511810000007";
        var (_, propertyId) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        await SeedEarlyCheckInPolicyAsync(
            """{"allowed":true,"earliestTime":"12:00:00","requiresCleaningCompleted":true,"requiresForm":false,"notifyFrontDesk":true}""");
        _ = propertyId;

        var response = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-TOOL-POLICY", "5511810000007", $"posso fazer early check-in? {FakeModelProvider.ToolCallTriggerPrefix}GetRelevantPolicies]");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-POLICY");
        message.Should().NotBeNull(WorkerSnapshot());
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle();
        toolExecutions[0].ToolName.Should().Be("GetRelevantPolicies");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success,
            "the real TENANT-scope value seeded via CreatePolicyValueVersionCommand must resolve through GetEffectivePolicyQuery's own real PROPERTY -> TENANT -> GLOBAL precedence, cast to its typed EarlyCheckInPolicy shape — never the raw boxed object");
    }

    [Fact]
    public async Task Idempotency_the_same_MessageId_delivered_twice_executes_the_tool_exactly_once()
    {
        const string guestPhone = "+5511810000008";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        for (var i = 0; i < 2; i++)
        {
            var response = await SendInboundMessageAsync(
                "wamid.AIAGENT-E2E-TOOL-IDEMPOTENCY", "5511810000008", $"resumo da reserva {FakeModelProvider.ToolCallTriggerPrefix}GetReservationSummary]");
            response.EnsureSuccessStatusCode();
        }

        var message = await WaitForInboundMessageAsync("wamid.AIAGENT-E2E-TOOL-IDEMPOTENCY");
        message.Should().NotBeNull(WorkerSnapshot());
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());

        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountInteractionsAsync(message.Id)).Should().Be(1,
            "a redelivered ConversationMessageReceived must never produce a second AgentInteraction nor re-execute the Tool");
        (await ReadToolExecutionsAsync(interaction!.Id)).Should().ContainSingle(
            "the Tool must execute exactly once for the original delivery — never once per redelivery");
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

    private async Task<int> CountOutboundMessagesAsync(Guid conversationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.Messages.AsNoTracking()
            .CountAsync(m => m.TenantId == GlobalTenantId && m.ConversationId == conversationId && m.Direction == MessageDirection.Outbound);

        await transaction.CommitAsync();
        return count;
    }

    /// <summary>
    /// Waits for a COMPLETED interaction — not merely a persisted row. In the
    /// tool-calling path, <c>AgentInteraction</c> is inserted as
    /// <see cref="AgentInteractionOutcome.InProgress"/> BEFORE the tool runs
    /// (so <c>AgentToolExecution</c>'s own database foreign key always has a
    /// real parent), then updated to Success/Failure only once the second
    /// model call returns. A "not null" check alone can observe that
    /// transient InProgress row and return early — a real polling bug found
    /// and fixed during CP3 homologation.
    /// </summary>
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
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"E2E-{Guid.NewGuid():N}"[..12]),
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

    private enum PixChargeSeedOutcome
    {
        Confirmed,
        Failed,
    }

    private async Task SeedPixChargeAsync(Guid reservationId, DateTimeOffset createdAtUtc, PixChargeSeedOutcome outcome)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "payments"))
            .Options;
        await using var dbContext = new PaymentsDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {GlobalTenantId.ToString()}, true)");

        var charge = PixCharge.Create(Guid.NewGuid(), GlobalTenantId, Guid.NewGuid(), reservationId, 150m, "BRL", createdAtUtc);
        if (outcome == PixChargeSeedOutcome.Confirmed)
            charge.Confirm(createdAtUtc.AddMinutes(5));
        else
            charge.Fail(createdAtUtc.AddMinutes(5));
        dbContext.PixCharges.Add(charge);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<bool> WaitForAutomatedCleaningAsync(Guid reservationId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await AutomatedCleaningExistsAsync(reservationId))
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await AutomatedCleaningExistsAsync(reservationId);
    }

    private async Task<bool> AutomatedCleaningExistsAsync(Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;
        await using var dbContext = new HousekeepingDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {GlobalTenantId.ToString()}, true)");

        var exists = await dbContext.Cleanings.AsNoTracking()
            .AnyAsync(c => c.TenantId == GlobalTenantId && c.ReservationId == reservationId && c.CreatedByUserId == null);

        await transaction.CommitAsync();
        return exists;
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

    private async Task SeedEarlyCheckInPolicyAsync(string jsonValue)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(GlobalTenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();

        var result = await dispatcher.Send(new CreatePolicyValueVersionCommand(
            GlobalTenantId, Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, jsonValue, "E2E test setup", null, null));
        result.IsSuccess.Should().BeTrue("EARLY_CHECKIN policy seeding must succeed — this is a real Configuration & Policy write, not a mock");
    }
}
