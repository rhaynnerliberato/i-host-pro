using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.AIAgent.Tests.Integration;

/// <summary>
/// Exercises the AI Agent Bounded Context's physical foundation against a
/// real PostgreSQL instance (Testcontainers): migration application,
/// Row-Level Security, the active-session partial unique index, the
/// interaction idempotency unique index, and the messaging schema's
/// provisioning (Fase 11, Checkpoint 2 — AI Agent Foundation). Mirrors
/// <c>PaymentsFoundationTests</c> exactly.
/// </summary>
public class AIAgentFoundationTests : IClassFixture<AIAgentFoundationTests.Fixture>
{
    private const string MessagingSchema = "ai_agent_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public AIAgentFoundationTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();

            var adminConnectionString = _container.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var dbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await dbContext.Database.MigrateAsync();
            }

            await ProvisionMessagingSchemaAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionMessagingSchemaAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(AIAgentDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Migration ----

    [Fact]
    public async Task Migration_applies_cleanly_and_creates_the_expected_tables()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        (await TableExistsAsync(dbContext, "ai_agent", "agent_sessions")).Should().BeTrue();
        (await TableExistsAsync(dbContext, "ai_agent", "agent_interactions")).Should().BeTrue();
        (await TableExistsAsync(dbContext, "ai_agent", "agent_tool_executions")).Should().BeTrue();
        (await TableExistsAsync(dbContext, "ai_agent", "agent_pending_actions")).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_is_idempotent_on_reapplication()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var act = async () => await dbContext.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Messaging_outbox_schema_is_provisioned()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        (await TableExistsAsync(dbContext, MessagingSchema, "wolverine_outgoing_envelopes")).Should().BeTrue();
    }

    // ---- Row-Level Security (AgentSession) ----

    [Fact]
    public async Task App_role_sees_only_its_own_tenant_AgentSession_rows()
    {
        var (tenantId, conversationId, _) = await SeedActiveSessionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var sessions = await dbContext.AgentSessions.Where(s => s.ConversationId == conversationId).ToListAsync();

        sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_AgentSession_rows()
    {
        var (_, conversationId, _) = await SeedActiveSessionAsync();
        var unrelatedTenantId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.AgentSessions.Where(s => s.ConversationId == conversationId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_setting_fails_closed_to_zero_AgentSession_rows_even_for_the_migrator_role()
    {
        await SeedActiveSessionAsync();

        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Deliberately no set_config('app.tenant_id', ...) call — RLS must fail closed.

        var visible = await dbContext.AgentSessions.IgnoreQueryFilters().ToListAsync();

        visible.Should().BeEmpty();
    }

    // ---- Active-session cardinality (governance resolution item 12/27) ----

    [Fact]
    public async Task A_second_Active_session_for_the_same_TenantId_ConversationId_is_rejected()
    {
        var (tenantId, conversationId, _) = await SeedActiveSessionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondSession = AgentSession.Create(Guid.NewGuid(), tenantId, conversationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.AgentSessions.Add(secondSession);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("only one Active AgentSession may exist per (TenantId, ConversationId)");
    }

    [Fact]
    public async Task A_second_Active_session_is_allowed_once_the_first_is_Completed()
    {
        var (tenantId, conversationId, firstSessionId) = await SeedActiveSessionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var completeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var completeTransaction = await completeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(completeDbContext, tenantId);
            var firstSession = await completeDbContext.AgentSessions.FirstAsync(s => s.Id == firstSessionId);
            firstSession.Complete(DateTimeOffset.UtcNow);
            await completeDbContext.SaveChangesAsync();
            await completeTransaction.CommitAsync();
        }

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondSession = AgentSession.Create(Guid.NewGuid(), tenantId, conversationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.AgentSessions.Add(secondSession);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Interaction idempotency (mandate item 19/28) ----

    [Fact]
    public async Task A_second_AgentInteraction_for_the_same_TenantId_InboundMessageId_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var agentSessionId = Guid.NewGuid();
        var inboundMessageId = Guid.NewGuid();
        await SeedInteractionAsync(tenantId, agentSessionId, inboundMessageId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var second = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, inboundMessageId, "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(second);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("the same ConversationMessageReceived/MessageId must never produce two AgentInteractions");
    }

    // ---- AgentToolExecution: real persistence + FK + RLS (Fase 11, Checkpoint 3) ----

    [Fact]
    public async Task AgentToolExecution_round_trips_ToolName_StartedAtUtc_CompletedAtUtc_and_Outcome()
    {
        var tenantId = Guid.NewGuid();
        var agentInteractionId = Guid.NewGuid();
        var inboundMessageId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var completedAt = startedAt.AddMilliseconds(180);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var writeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var writeTransaction = await writeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(writeDbContext, tenantId);

            var interaction = AgentInteraction.Start(agentInteractionId, tenantId, Guid.NewGuid(), inboundMessageId, "Fake", "fake-model-v1", startedAt);
            writeDbContext.AgentInteractions.Add(interaction);

            var execution = AgentToolExecution.Start(Guid.NewGuid(), tenantId, agentInteractionId, "GetReservationSummary", startedAt);
            execution.CompleteSuccessfully(completedAt);
            writeDbContext.AgentToolExecutions.Add(execution);

            await writeDbContext.SaveChangesAsync();
            await writeTransaction.CommitAsync();
        }

        await using var readDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var readTransaction = await readDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(readDbContext, tenantId);

        var persisted = await readDbContext.AgentToolExecutions.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.AgentInteractionId == agentInteractionId);

        persisted.ToolName.Should().Be("GetReservationSummary");
        // BeCloseTo, not Be: Postgres timestamptz truncates to microsecond
        // precision, coarser than DateTimeOffset.UtcNow's own tick resolution.
        persisted.StartedAtUtc.Should().BeCloseTo(startedAt, TimeSpan.FromMilliseconds(1));
        persisted.CompletedAtUtc.Should().BeCloseTo(completedAt, TimeSpan.FromMilliseconds(1));
        persisted.Outcome.Should().Be(AgentToolExecutionOutcome.Success);
        persisted.DurationMs.Should().Be(180);
    }

    [Fact]
    public async Task AgentToolExecution_rejects_a_reference_to_a_nonexistent_AgentInteraction()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var execution = AgentToolExecution.Start(Guid.NewGuid(), tenantId, Guid.NewGuid(), "GetReservationSummary", DateTimeOffset.UtcNow);
        dbContext.AgentToolExecutions.Add(execution);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "agent_tool_executions.agent_interaction_id carries a real database foreign key — the parent AgentInteraction row must already exist");
    }

    [Fact]
    public async Task App_role_sees_only_its_own_tenant_AgentToolExecution_rows()
    {
        var (tenantId, agentInteractionId) = await SeedToolExecutionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var visible = await dbContext.AgentToolExecutions.Where(e => e.AgentInteractionId == agentInteractionId).ToListAsync();

        visible.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_AgentToolExecution_rows()
    {
        var (_, agentInteractionId) = await SeedToolExecutionAsync();
        var unrelatedTenantId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.AgentToolExecutions.Where(e => e.AgentInteractionId == agentInteractionId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_setting_fails_closed_to_zero_AgentToolExecution_rows_even_for_the_migrator_role()
    {
        await SeedToolExecutionAsync();

        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Deliberately no set_config('app.tenant_id', ...) call — RLS must fail closed.

        var visible = await dbContext.AgentToolExecutions.IgnoreQueryFilters().ToListAsync();

        visible.Should().BeEmpty();
    }

    // ---- AgentPendingAction: real persistence + FK + partial unique index + RLS (Fase 11, Checkpoint 4) ----

    [Fact]
    public async Task AgentPendingAction_round_trips_ToolName_SanitizedArguments_and_Status()
    {
        var tenantId = Guid.NewGuid();
        var agentSessionId = Guid.NewGuid();
        var proposedByInteractionId = Guid.NewGuid();
        const string sanitizedArguments = """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""";

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var writeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var writeTransaction = await writeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(writeDbContext, tenantId);

            var interaction = AgentInteraction.Start(proposedByInteractionId, tenantId, agentSessionId, Guid.NewGuid(), "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
            writeDbContext.AgentInteractions.Add(interaction);

            var pendingAction = AgentPendingAction.Propose(
                Guid.NewGuid(), tenantId, agentSessionId, proposedByInteractionId, "RequestEarlyCheckIn", sanitizedArguments, DateTimeOffset.UtcNow);
            writeDbContext.AgentPendingActions.Add(pendingAction);

            await writeDbContext.SaveChangesAsync();
            await writeTransaction.CommitAsync();
        }

        await using var readDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var readTransaction = await readDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(readDbContext, tenantId);

        var persisted = await readDbContext.AgentPendingActions.AsNoTracking()
            .SingleAsync(a => a.TenantId == tenantId && a.AgentSessionId == agentSessionId);

        persisted.ToolName.Should().Be("RequestEarlyCheckIn");
        persisted.SanitizedArguments.Should().Be(sanitizedArguments);
        persisted.Status.Should().Be(AgentPendingActionStatus.Proposed);
        persisted.ProposedByInteractionId.Should().Be(proposedByInteractionId);
    }

    [Fact]
    public async Task AgentPendingAction_rejects_a_reference_to_a_nonexistent_AgentInteraction()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var pendingAction = AgentPendingAction.Propose(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), Guid.NewGuid(), "RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", DateTimeOffset.UtcNow);
        dbContext.AgentPendingActions.Add(pendingAction);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>(
            "agent_pending_actions.proposed_by_interaction_id carries a real database foreign key — the parent AgentInteraction row must already exist");
    }

    [Fact]
    public async Task A_second_active_AgentPendingAction_for_the_same_AgentSession_is_rejected()
    {
        var (tenantId, agentSessionId) = await SeedActivePendingActionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondInteraction = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, Guid.NewGuid(), "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(secondInteraction);
        var secondPendingAction = AgentPendingAction.Propose(
            Guid.NewGuid(), tenantId, agentSessionId, secondInteraction.Id, "RequestLateCheckout", """{"requestedCheckOutAt":"2026-09-05T14:00:00Z"}""", DateTimeOffset.UtcNow);
        dbContext.AgentPendingActions.Add(secondPendingAction);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("only one active (Proposed/Confirmed) AgentPendingAction may exist per AgentSession");
    }

    [Fact]
    public async Task A_second_AgentPendingAction_is_allowed_once_the_first_is_Executed()
    {
        var (tenantId, agentSessionId) = await SeedActivePendingActionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var completeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var completeTransaction = await completeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(completeDbContext, tenantId);
            var first = await completeDbContext.AgentPendingActions.FirstAsync(a => a.AgentSessionId == agentSessionId);
            first.Confirm(DateTimeOffset.UtcNow);
            first.MarkExecuted(DateTimeOffset.UtcNow);
            await completeDbContext.SaveChangesAsync();
            await completeTransaction.CommitAsync();
        }

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondInteraction = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, Guid.NewGuid(), "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(secondInteraction);
        var secondPendingAction = AgentPendingAction.Propose(
            Guid.NewGuid(), tenantId, agentSessionId, secondInteraction.Id, "RequestLateCheckout", """{"requestedCheckOutAt":"2026-09-05T14:00:00Z"}""", DateTimeOffset.UtcNow);
        dbContext.AgentPendingActions.Add(secondPendingAction);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task App_role_sees_only_its_own_tenant_AgentPendingAction_rows()
    {
        var (tenantId, agentSessionId) = await SeedActivePendingActionAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var visible = await dbContext.AgentPendingActions.Where(a => a.AgentSessionId == agentSessionId).ToListAsync();

        visible.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_AgentPendingAction_rows()
    {
        var (_, agentSessionId) = await SeedActivePendingActionAsync();
        var unrelatedTenantId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.AgentPendingActions.Where(a => a.AgentSessionId == agentSessionId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_setting_fails_closed_to_zero_AgentPendingAction_rows_even_for_the_migrator_role()
    {
        await SeedActivePendingActionAsync();

        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Deliberately no set_config('app.tenant_id', ...) call — RLS must fail closed.

        var visible = await dbContext.AgentPendingActions.IgnoreQueryFilters().ToListAsync();

        visible.Should().BeEmpty();
    }

    // ---- AgentInteraction.OutboundMessageId (Fase 11, Checkpoint 4) ----

    [Fact]
    public async Task AgentInteraction_OutboundMessageId_round_trips_and_defaults_to_null()
    {
        var tenantId = Guid.NewGuid();
        var inboundMessageId = Guid.NewGuid();
        var outboundMessageId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var writeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var writeTransaction = await writeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(writeDbContext, tenantId);
            var interaction = AgentInteraction.Start(Guid.NewGuid(), tenantId, Guid.NewGuid(), inboundMessageId, "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
            interaction.CompleteSuccessfully(DateTimeOffset.UtcNow, intent: null, language: "pt-BR", confidence: null, inputTokens: 1, outputTokens: 1);
            writeDbContext.AgentInteractions.Add(interaction);
            await writeDbContext.SaveChangesAsync();
            await writeTransaction.CommitAsync();
        }

        await using (var readDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var readTransaction = await readDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(readDbContext, tenantId);
            var persisted = await readDbContext.AgentInteractions.AsNoTracking().SingleAsync(i => i.InboundMessageId == inboundMessageId);
            persisted.OutboundMessageId.Should().BeNull();
        }

        await using (var updateDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var updateTransaction = await updateDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(updateDbContext, tenantId);
            var interaction = await updateDbContext.AgentInteractions.SingleAsync(i => i.InboundMessageId == inboundMessageId);
            interaction.RecordOutboundMessage(outboundMessageId);
            await updateDbContext.SaveChangesAsync();
            await updateTransaction.CommitAsync();
        }

        await using var finalDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var finalTransaction = await finalDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(finalDbContext, tenantId);
        var final = await finalDbContext.AgentInteractions.AsNoTracking().SingleAsync(i => i.InboundMessageId == inboundMessageId);
        final.OutboundMessageId.Should().Be(outboundMessageId);
    }

    // ---- Confidence persistence round-trip (mandate item 35) ----

    [Fact]
    public async Task Confidence_persistence_round_trip_preserves_the_exact_decimal_value()
    {
        var tenantId = Guid.NewGuid();
        var agentSessionId = Guid.NewGuid();
        var inboundMessageId = Guid.NewGuid();
        const decimal confidence = 0.7534m;

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var writeDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var writeTransaction = await writeDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(writeDbContext, tenantId);
            var interaction = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, inboundMessageId, "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
            interaction.CompleteSuccessfully(DateTimeOffset.UtcNow, intent: null, language: "pt-BR", confidence, inputTokens: 10, outputTokens: 20);
            writeDbContext.AgentInteractions.Add(interaction);
            await writeDbContext.SaveChangesAsync();
            await writeTransaction.CommitAsync();
        }

        await using var readDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var readTransaction = await readDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(readDbContext, tenantId);

        var persisted = await readDbContext.AgentInteractions.AsNoTracking()
            .SingleAsync(i => i.TenantId == tenantId && i.InboundMessageId == inboundMessageId);

        persisted.Confidence.Should().Be(confidence);
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid ConversationId, Guid SessionId)> SeedActiveSessionAsync()
    {
        var tenantId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var session = AgentSession.Create(Guid.NewGuid(), tenantId, conversationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.AgentSessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, conversationId, session.Id);
    }

    private async Task SeedInteractionAsync(Guid tenantId, Guid agentSessionId, Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var interaction = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, inboundMessageId, "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(interaction);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<(Guid TenantId, Guid AgentInteractionId)> SeedToolExecutionAsync()
    {
        var tenantId = Guid.NewGuid();
        var agentInteractionId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var interaction = AgentInteraction.Start(agentInteractionId, tenantId, Guid.NewGuid(), Guid.NewGuid(), "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(interaction);

        var execution = AgentToolExecution.Start(Guid.NewGuid(), tenantId, agentInteractionId, "GetReservationSummary", DateTimeOffset.UtcNow);
        execution.CompleteSuccessfully(DateTimeOffset.UtcNow);
        dbContext.AgentToolExecutions.Add(execution);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, agentInteractionId);
    }

    private async Task<(Guid TenantId, Guid AgentSessionId)> SeedActivePendingActionAsync()
    {
        var tenantId = Guid.NewGuid();
        var agentSessionId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var interaction = AgentInteraction.Start(Guid.NewGuid(), tenantId, agentSessionId, Guid.NewGuid(), "Fake", "fake-model-v1", DateTimeOffset.UtcNow);
        dbContext.AgentInteractions.Add(interaction);

        var pendingAction = AgentPendingAction.Propose(
            Guid.NewGuid(), tenantId, agentSessionId, interaction.Id, "RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", DateTimeOffset.UtcNow);
        dbContext.AgentPendingActions.Add(pendingAction);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, agentSessionId);
    }

    private static async Task SetTenantAsync(AIAgentDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static async Task<bool> TableExistsAsync(AIAgentDbContext dbContext, string schema, string table)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await dbContext.Database.OpenConnectionAsync();

        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table)";
        var schemaParam = command.CreateParameter();
        schemaParam.ParameterName = "schema";
        schemaParam.Value = schema;
        command.Parameters.Add(schemaParam);
        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "table";
        tableParam.Value = table;
        command.Parameters.Add(tableParam);

        var result = await command.ExecuteScalarAsync();
        return result is true;
    }

    private static AIAgentDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<AIAgentDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent"))
            .Options;

        return new AIAgentDbContext(options, tenantContext);
    }
}
