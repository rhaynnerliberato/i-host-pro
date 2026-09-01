using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 6 (Human Handoff, Safety &amp; Audit) — mandatory real
/// transport E2E gate. Reuses <see cref="ConversationMessageReceivedWorkflowRoundTripTests.Fixture"/>
/// verbatim (own container instance, same shape as every other CP2-CP5 E2E
/// class in this directory).
///
/// What these scenarios prove, end-to-end through the real Api → outbox →
/// RabbitMQ → Worker → AI Agent consumer pipeline, plus the real
/// <c>IHostPro.Api</c>-hosted Resume endpoint: an explicit human-request
/// intent creates a genuine <see cref="AgentHumanHandoff"/>, escalates the
/// <see cref="AgentSession"/>, and — when a real
/// <c>AdministratorNotificationContact</c> is seeded — actually notifies the
/// administrator (item 57/65); when no contact exists, the handoff stays
/// <see cref="AgentHumanHandoffStatus.Requested"/> and the guest ack never
/// overclaims (item 66); an active <see cref="AgentPendingAction"/> is
/// cancelled, never executed, the moment a handoff begins (item 64); a
/// follow-up guest message on an already-escalated session never reaches the
/// model or any Tool — including one that itself carries a tool-call/prompt-
/// injection marker — and never creates a second handoff (items 62/63/71);
/// the real authenticated Resume endpoint (<c>AI_AGENT:MANAGE</c>) reactivates
/// the session and the very next message is processed normally again (item
/// 68/69); and a caller without that permission is genuinely forbidden (item
/// 70).
/// </summary>
public sealed class AIAgentHumanHandoffWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-aiagent-phone-number-id";
    private const string AppSecret = "e2e-aiagent-test-app-secret";

    private readonly ConversationMessageReceivedWorkflowRoundTripTests.Fixture _fixture;

    public AIAgentHumanHandoffWorkflowRoundTripTests(ConversationMessageReceivedWorkflowRoundTripTests.Fixture fixture) => _fixture = fixture;

    private static readonly Guid GlobalTenantId = ConversationMessageReceivedWorkflowRoundTripTests.GlobalTenantId;

    [Fact]
    public async Task Explicit_human_request_creates_a_real_handoff_escalates_the_session_and_notifies_the_administrator()
    {
        const string guestPhone = "+5511840000001";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        await SeedAdministratorContactAsync("+5511999990001");

        var response = await SendInboundMessageAsync(
            "wamid.CP6-HANDOFF-NOTIFIED", "5511840000001", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP6-HANDOFF-NOTIFIED");
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().BeEmpty("a restricted intent preempts tool-call handling entirely");

        var session = await ReadSessionByIdAsync(interaction.AgentSessionId);
        session!.Status.Should().Be(AgentSessionStatus.Escalated, WorkerSnapshot());

        var handoff = await WaitForHandoffAsync(interaction.AgentSessionId, h => h.Status == AgentHumanHandoffStatus.Notified);
        handoff.Should().NotBeNull("a real, seeded AdministratorNotificationContact must let notification genuinely succeed — " + WorkerSnapshot());
        handoff!.ReasonCode.Should().Be(AgentHumanHandoffReasonCode.ExplicitHumanRequest);
        handoff.NotifiedAtUtc.Should().NotBeNull();

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        var outboundMessage = await ReadMessageByIdAsync(withOutboundMessage!.Value);
        outboundMessage!.RenderedContent.Should().Contain("encaminhada", "the ack must reflect that notification genuinely succeeded");
    }

    [Fact]
    public async Task Handoff_without_an_administrator_contact_stays_Requested_and_the_ack_never_overclaims()
    {
        const string guestPhone = "+5511840000002";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        // Deliberately no AdministratorNotificationContact seeded — notification must genuinely fail.

        var response = await SendInboundMessageAsync(
            "wamid.CP6-HANDOFF-NO-CONTACT", "5511840000002", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.CP6-HANDOFF-NO-CONTACT");
        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var session = await ReadSessionByIdAsync(interaction.AgentSessionId);
        session!.Status.Should().Be(AgentSessionStatus.Escalated, "the session is still suspended even though notification failed — mandate item 9: never a rollback");

        var handoff = await WaitForHandoffAsync(interaction.AgentSessionId, h => h.NotificationAttemptedAtUtc is not null);
        handoff!.Status.Should().Be(AgentHumanHandoffStatus.Requested, "notification never succeeded — the handoff must never be reported as Notified");
        handoff.NotificationFailureCode.Should().NotBeNullOrWhiteSpace();

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(message.Id);
        var outboundMessage = await ReadMessageByIdAsync(withOutboundMessage!.Value);
        outboundMessage!.RenderedContent.Should().NotContain("encaminhada", "the ack must never claim a notification that did not actually happen");
    }

    [Fact]
    public async Task An_active_pending_action_is_cancelled_never_executed_when_a_handoff_begins()
    {
        const string guestPhone = "+5511840000003";
        var (reservationId, _) = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        var requestedCheckInAt = DateTimeOffset.UtcNow.AddDays(29).ToString("O");

        var proposeResponse = await SendInboundMessageAsync(
            "wamid.CP6-PENDING-PROPOSE", "5511840000003",
            $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={requestedCheckInAt}]");
        proposeResponse.EnsureSuccessStatusCode();
        var proposeMessage = await WaitForInboundMessageAsync("wamid.CP6-PENDING-PROPOSE");
        await WaitForInteractionAsync(proposeMessage!.Id);

        var pendingAction = await WaitForActivePendingActionAsync(reservationId);
        pendingAction.Should().NotBeNull(WorkerSnapshot());
        pendingAction!.Status.Should().Be(AgentPendingActionStatus.Proposed);

        var handoffResponse = await SendInboundMessageAsync(
            "wamid.CP6-PENDING-HANDOFF", "5511840000003", $"na verdade quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        handoffResponse.EnsureSuccessStatusCode();
        var handoffMessage = await WaitForInboundMessageAsync("wamid.CP6-PENDING-HANDOFF");
        var handoffInteraction = await WaitForInteractionAsync(handoffMessage!.Id);
        handoffInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());

        var cancelledPendingAction = await WaitForPendingActionStatusAsync(pendingAction.Id, AgentPendingActionStatus.Cancelled);
        cancelledPendingAction!.Status.Should().Be(
            AgentPendingActionStatus.Cancelled, "a handoff must cancel any active pending action, never preserve or execute it — " + WorkerSnapshot());

        (await CountEarlyCheckInRequestsAsync(reservationId)).Should().Be(0, "the cancelled proposal's own Command must never run");
    }

    [Fact]
    public async Task A_follow_up_message_on_an_escalated_session_never_reaches_the_model_or_any_tool_and_never_creates_a_second_handoff()
    {
        const string guestPhone = "+5511840000004";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var firstResponse = await SendInboundMessageAsync(
            "wamid.CP6-SUSPENDED-1", "5511840000004", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        firstResponse.EnsureSuccessStatusCode();
        var firstMessage = await WaitForInboundMessageAsync("wamid.CP6-SUSPENDED-1");
        var firstInteraction = await WaitForInteractionAsync(firstMessage!.Id);
        firstInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        await WaitForHandoffAsync(firstInteraction.AgentSessionId, h => h.NotificationAttemptedAtUtc is not null);

        // Deliberately embeds a tool-call marker — proves the suspended-session
        // guard intercepts BEFORE the model/any Tool ever sees this content,
        // not merely that the model chose not to act on it (mandate item 71).
        var secondResponse = await SendInboundMessageAsync(
            "wamid.CP6-SUSPENDED-2", "5511840000004",
            $"por favor {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt={DateTimeOffset.UtcNow.AddDays(29):O}]");
        secondResponse.EnsureSuccessStatusCode();
        var secondMessage = await WaitForInboundMessageAsync("wamid.CP6-SUSPENDED-2");
        var secondInteraction = await WaitForInteractionAsync(secondMessage!.Id);

        secondInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        secondInteraction.AgentSessionId.Should().Be(firstInteraction.AgentSessionId, "the SAME escalated session must be reused, never a new one");
        secondInteraction.Intent.Should().BeNull("the suspended-session path never calls the model — there is no intent to classify");

        (await ReadToolExecutionsAsync(secondInteraction.Id)).Should().BeEmpty(
            "zero model/Tool calls on an already-escalated session, even with an embedded tool-call marker — " + WorkerSnapshot());

        (await CountHandoffsAsync(firstInteraction.AgentSessionId)).Should().Be(1, "a duplicate trigger while already escalated must never create a second handoff");

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(secondMessage.Id);
        withOutboundMessage.Should().NotBeNull("the suspended-session path still delivers a deterministic ack — " + WorkerSnapshot());
    }

    [Fact]
    public async Task Resume_via_the_real_authenticated_endpoint_reactivates_the_session_and_the_next_message_is_processed_normally()
    {
        const string guestPhone = "+5511840000005";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var handoffResponse = await SendInboundMessageAsync(
            "wamid.CP6-RESUME-1", "5511840000005", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        handoffResponse.EnsureSuccessStatusCode();
        var handoffMessage = await WaitForInboundMessageAsync("wamid.CP6-RESUME-1");
        var handoffInteraction = await WaitForInteractionAsync(handoffMessage!.Id);
        var sessionId = handoffInteraction!.AgentSessionId;
        await WaitForHandoffAsync(sessionId, h => h.NotificationAttemptedAtUtc is not null);

        var actorId = Guid.NewGuid();
        var token = await GenerateTokenAsync(actorId, roles: ["ADMIN"]);
        var resumeResponse = await PostAsync($"/api/v1/ai-agent/sessions/{sessionId:D}/resume", token);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(resumeResponse));

        var resumedSession = await ReadSessionByIdAsync(sessionId);
        resumedSession!.Status.Should().Be(AgentSessionStatus.Active, "a successful Resume must reactivate the session");

        var resumedHandoff = await ReadHandoffByAgentSessionIdAsync(sessionId);
        resumedHandoff!.Status.Should().Be(AgentHumanHandoffStatus.Resumed);
        resumedHandoff.ResumedByActorId.Should().Be(actorId, "the real authenticated actor, never a caller-supplied value");

        var nextResponse = await SendInboundMessageAsync("wamid.CP6-RESUME-2", "5511840000005", "obrigado, pode continuar");
        nextResponse.EnsureSuccessStatusCode();
        var nextMessage = await WaitForInboundMessageAsync("wamid.CP6-RESUME-2");
        var nextInteraction = await WaitForInteractionAsync(nextMessage!.Id);

        nextInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        nextInteraction.AgentSessionId.Should().Be(sessionId, "the same session, now Active again, must be reused — never a new one");

        var withOutboundMessage = await WaitForOutboundMessageIdAsync(nextMessage.Id);
        var outboundMessage = await ReadMessageByIdAsync(withOutboundMessage!.Value);
        outboundMessage!.RenderedContent.Should().NotContain("pausado", "a resumed session must answer normally again, never with the suspended-session ack");
    }

    [Fact]
    public async Task Resume_without_the_AI_AGENT_MANAGE_permission_is_forbidden()
    {
        const string guestPhone = "+5511840000006";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var handoffResponse = await SendInboundMessageAsync(
            "wamid.CP6-FORBIDDEN", "5511840000006", $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}");
        handoffResponse.EnsureSuccessStatusCode();
        var handoffMessage = await WaitForInboundMessageAsync("wamid.CP6-FORBIDDEN");
        var handoffInteraction = await WaitForInteractionAsync(handoffMessage!.Id);
        var sessionId = handoffInteraction!.AgentSessionId;
        await WaitForHandoffAsync(sessionId, h => h.NotificationAttemptedAtUtc is not null);

        var token = await GenerateTokenAsync(Guid.NewGuid(), roles: ["OPERATOR"]);
        var resumeResponse = await PostAsync($"/api/v1/ai-agent/sessions/{sessionId:D}/resume", token);

        resumeResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, "OPERATOR is never granted AI_AGENT:MANAGE — only ADMIN is");

        var untouchedSession = await ReadSessionByIdAsync(sessionId);
        untouchedSession!.Status.Should().Be(AgentSessionStatus.Escalated, "a forbidden Resume attempt must never actually reactivate the session");
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

    private async Task<string> GenerateTokenAsync(Guid userId, IReadOnlyCollection<string> roles)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: GlobalTenantId, SessionId: Guid.NewGuid(), Roles: roles);
        return generator.GenerateAccessToken(request).Token;
    }

    private async Task<HttpResponseMessage> PostAsync(string route, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _fixture.ApiClient.SendAsync(request);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return "(unreadable body)"; }
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

    private async Task<AgentHumanHandoff?> WaitForHandoffAsync(Guid agentSessionId, Func<AgentHumanHandoff, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var handoff = await ReadHandoffByAgentSessionIdAsync(agentSessionId);
            if (handoff is not null && predicate(handoff))
                return handoff;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadHandoffByAgentSessionIdAsync(agentSessionId);
    }

    private async Task<int> CountHandoffsAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.AgentHumanHandoffs.AsNoTracking().CountAsync(h => h.AgentSessionId == agentSessionId);

        await transaction.CommitAsync();
        return count;
    }

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

    private async Task<AgentPendingAction?> WaitForPendingActionStatusAsync(Guid id, AgentPendingActionStatus status)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var pendingAction = await ReadPendingActionByIdAsync(id);
            if (pendingAction is not null && pendingAction.Status == status)
                return pendingAction;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadPendingActionByIdAsync(id);
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

    private GuestOperationsDbContext CreateGuestOperationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;
        return new GuestOperationsDbContext(options, tenantContext);
    }

    private async Task SeedAdministratorContactAsync(string phone)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), GlobalTenantId, phone, DateTimeOffset.UtcNow);
        dbContext.AdministratorNotificationContacts.Add(contact);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"CP6-{Guid.NewGuid():N}"[..12]),
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
