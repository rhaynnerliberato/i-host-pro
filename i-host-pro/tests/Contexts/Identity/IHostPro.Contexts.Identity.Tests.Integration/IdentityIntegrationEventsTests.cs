using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end coverage of the six real Integration Events published by Login,
/// Logout and Refresh Token exchange (Incremento 2 plan, Etapa 15; Documento
/// 07 §13.1; ADR-013) against real PostgreSQL and RabbitMQ: exact event and
/// payload per business branch, no-event branches, atomicity/rollback,
/// no-duplication under retry, and outage/recovery of the broker. Separate
/// from <see cref="LoginCommandHandlerTests"/>/<see cref="LogoutCommandHandlerTests"/>
/// (which cover the underlying business logic and do not exercise the
/// outbox), and from <see cref="LoginTenantAwareBehaviorTests"/> (which
/// covers atomicity of the transactional executor itself using only the
/// canary event) — this file's job is the six real events specifically.
/// </summary>
public class IdentityIntegrationEventsTests : IClassFixture<IdentityIntegrationEventsTests.Fixture>
{
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string KnownSecret = "known-secret-segment-for-tests";
    private const string OutboxSchema = "identity_messaging";

    private readonly Fixture _fixture;

    public IdentityIntegrationEventsTests(Fixture fixture) => _fixture = fixture;

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale. No test in this class stops or restarts this
    /// shared <see cref="RabbitMqContainer"/> — the two tests that simulate a
    /// broker outage each spin up and dispose their own dedicated container
    /// instead (see <see cref="IdentityOutboxTransactionExecutorTests"/>'s
    /// equivalent doc comment for why).
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer PostgresContainer { get; private set; } = null!;
        public RabbitMqContainer RabbitMqContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            PostgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            RabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();

            await Task.WhenAll(PostgresContainer.StartAsync(), RabbitMqContainer.StartAsync());

            var adminConnectionString = PostgresContainer.GetConnectionString();

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

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync()
        {
            await PostgresContainer.DisposeAsync();
            await RabbitMqContainer.DisposeAsync();
        }

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var grantCommand = connection.CreateCommand();
            grantCommand.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await grantCommand.ExecuteNonQueryAsync();
        }
    }

    // ---- Service graph -----------------------------------------------

    private async Task<IHost> BuildHostAsync(
        int maxFailedAccessAttempts = 5, TimeSpan? graceWindow = null, RabbitMqContainer? rabbitMqContainer = null)
    {
        rabbitMqContainer ??= _fixture.RabbitMqContainer;
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _fixture.AppConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKey.ExportRSAPrivateKeyPem(),
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = maxFailedAccessAttempts.ToString(),
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = (graceWindow ?? TimeSpan.FromSeconds(10)).ToString(),
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        hostBuilder.Services.AddIdentityJwtIssuance(configuration);
        hostBuilder.Services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        hostBuilder.Services.AddScoped<IIdentityTransactionExecutor, IdentityOutboxTransactionExecutor>();
        hostBuilder.Services.AddScoped<IRefreshTokenExchangeExecutor, RefreshTokenExchangeExecutor>();
        hostBuilder.Services.AddScoped<ILogoutExecutor, LogoutExecutor>();
        hostBuilder.Services.AddScoped<LoginCommandHandler>();
        hostBuilder.Services.AddScoped<LogoutCommandHandler>();
        hostBuilder.Services.AddScoped<RefreshTokenCommandHandler>();

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            // Mirrors IHostPro.Api/Program.cs exactly (Documento 07 §13.2;
            // ADR-013): one topic exchange per Bounded Context, routing key =
            // event name in snake_case, every route explicit about
            // .UseDurableOutbox() — confirmed empirically (Etapa 15A) that
            // without it, Wolverine defaults to "Inline" sending, which
            // discards the message after a few synchronous retries during a
            // broker outage instead of persisting it durably.
            //
            // .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1) — see
            // Program.cs's matching comment: caps the synchronous, awaited
            // opportunistic delivery attempt after commit to a single try
            // (Wolverine's own public API, DurableSendingAgent's default is 3)
            // before latching and deferring the rest to the Durability Agent.
            const string identityEventsExchange = "identity-events";

            void RouteIdentityEvent<TEvent>(string routingKey)
                where TEvent : IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent =>
                opts.PublishMessage(typeof(TEvent))
                    .ToRabbitRoutingKey(identityEventsExchange, routingKey, exchange => exchange.ExchangeType = ExchangeType.Topic)
                    .UseDurableOutbox()
                    .CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1);

            RouteIdentityEvent<UserLoggedIn>("user_logged_in");
            RouteIdentityEvent<LoginFailed>("login_failed");
            RouteIdentityEvent<AccountLockedOut>("account_locked_out");
            RouteIdentityEvent<UserLoggedOut>("user_logged_out");
            RouteIdentityEvent<RefreshTokenReuseDetected>("refresh_token_reuse_detected");
            RouteIdentityEvent<SessionRevoked>("session_revoked");
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Every test-built <see cref="IHost"/> in this class must be stopped
    /// this way — see <see cref="IdentityOutboxTransactionExecutorTests"/>'s
    /// equivalent helper for the full rationale.
    /// </summary>
    private static async Task StopGracefullyAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    // ---- Execute helpers ------------------------------------------------

    private static async Task<Result<AuthTokensResult>> ExecuteLoginAsync(
        IHost root, LoginCommand command, Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<IIdentityTransactionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<LoginCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteLogoutAsync(
        IHost root, LogoutCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<ILogoutExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<LogoutCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result<AuthTokensResult>> ExecuteRefreshAsync(
        IHost root, RefreshTokenCommand command, Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<IRefreshTokenExchangeExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RefreshTokenCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    /// <summary>
    /// Pauses the shared broker for the duration of <paramref name="action"/>
    /// so the envelope it publishes stays visible in the outgoing table long
    /// enough to inspect — confirmed empirically (Etapa 15A) that with
    /// RabbitMQ reachable, Wolverine's Durability Agent flushes/deletes an
    /// outgoing envelope almost immediately after commit, racing any query
    /// for it. Only needed for tests that assert an event WAS published; a
    /// test asserting one was NOT published needs no pausing (absence is
    /// absence regardless of relay timing).
    /// </summary>
    private async Task<T> WithBrokerPausedAsync<T>(Func<Task<T>> action)
    {
        await _fixture.RabbitMqContainer.StopAsync();
        try
        {
            return await action();
        }
        finally
        {
            await _fixture.RabbitMqContainer.StartAsync();
        }
    }

    // ---- Seeding ----------------------------------------------------------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
    }

    private async Task<(Guid UserId, string Email)> SeedUserAsync(Guid tenantId, bool blocked = false)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var email = $"{Guid.NewGuid():N}@ihostpro.com";

        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Test User", hash, DateTimeOffset.UtcNow);
        if (blocked)
            user.Block(DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (user.Id, email);
    }

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    private static (string Presented, Guid TokenId, string TokenHash) BuildPresentedToken(
        Guid tenantId, Guid? tokenId = null, string secret = KnownSecret)
    {
        var id = tokenId ?? Guid.NewGuid();
        var presented = $"{tenantId:N}.{id:N}.{secret}";
        var hash = new RefreshTokenHasher().ComputeHash(presented);
        return (presented, id, hash);
    }

    private async Task<Guid> SeedRefreshTokenAsync(
        Guid tenantId, Guid userId, Guid sessionId, Guid tokenId, string tokenHash,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, Action<RefreshToken>? mutate = null)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var token = RefreshToken.Issue(Guid.NewGuid(), tokenId, tenantId, sessionId, userId, tokenHash, issuedAt, expiresAt);
        mutate?.Invoke(token);
        dbContext.RefreshTokens.Add(token);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return token.Id;
    }

    private static LoginCommand LoginAs(string tenantSlug, string email, string password = KnownPassword) =>
        new(tenantSlug, email, password, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

    // ---- Outbox inspection ------------------------------------------------

    private async Task<long> CountOutgoingEnvelopesAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Every envelope currently in the outbox whose message type EXACTLY
    /// matches <typeparamref name="TEvent"/>'s full CLR type name. Exact
    /// equality on <c>message_type</c>, never a fuzzy/substring match:
    /// confirmed empirically that an <c>ILIKE '%...%'</c> fragment search
    /// (e.g. "sessionrevoked") can collide with an unrelated envelope.
    ///
    /// <c>wolverine_outgoing_envelopes.body</c> is NOT the message's JSON —
    /// confirmed empirically (Etapa 15 stabilization pass) that it is
    /// Wolverine's own binary envelope wire format: a sequence of
    /// length-prefixed header key/value pairs (<c>source</c>,
    /// <c>message-type</c>, <c>reply-uri</c>, ...) followed by the message's
    /// actual serialized JSON, and — also confirmed empirically — sometimes
    /// followed by further framing bytes after that JSON (e.g. when
    /// Wolverine batches more than one envelope's data together), so a naive
    /// first-<c>{</c>-to-last-<c>}</c> slice can span past the real object's
    /// end. <see cref="ExtractJsonObject"/> instead scans forward from the
    /// first <c>{</c> with a balanced brace counter to find that object's own
    /// matching closing <c>}</c>, ignoring anything beyond it.
    /// </summary>
    private async Task<List<JsonDocument>> FindEnvelopesAsync<TEvent>()
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT body FROM {OutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type = @messageType
            """;
        command.Parameters.AddWithValue("messageType", typeof(TEvent).FullName!);

        var results = new List<JsonDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var bytes = (byte[])reader[0];
            results.Add(ExtractJsonObject(bytes, typeof(TEvent).FullName!));
        }
        return results;
    }

    /// <summary>
    /// Finds the message's real JSON object inside <c>body</c>'s binary
    /// envelope framing. The first <c>{</c> byte in the array is NOT
    /// necessarily the JSON's start — Wolverine's header section is a
    /// sequence of length-prefixed key/value pairs, and a length-prefix byte
    /// can incidentally equal <c>0x7B</c> ('{'), especially with larger
    /// bodies (confirmed empirically: this collided in practice for
    /// <c>LoginFailed</c> envelopes in the multi-attempt lockout tests, where
    /// several same-type envelopes with longer reason-code strings raise the
    /// chance of a coincidental match). A candidate start is only accepted
    /// once its balanced-brace span also parses as syntactically valid JSON —
    /// scanning from a wrong (binary) starting byte essentially never
    /// produces both a balanced brace count AND valid JSON syntax, so this is
    /// deterministic in practice even though a single first-match scan is
    /// not.
    /// </summary>
    private static JsonDocument ExtractJsonObject(byte[] bytes, string messageTypeForDiagnostics)
    {
        var searchFrom = 0;
        while (true)
        {
            var start = Array.IndexOf(bytes, (byte)'{', searchFrom);
            if (start < 0)
            {
                throw new InvalidOperationException(
                    $"No JSON object found in the envelope body for message_type={messageTypeForDiagnostics} (length={bytes.Length}).");
            }

            var end = FindBalancedEnd(bytes, start);
            if (end is int endIndex)
            {
                try
                {
                    return JsonDocument.Parse(bytes.AsMemory(start, endIndex - start + 1));
                }
                catch (JsonException)
                {
                    // Balanced but not valid JSON — a coincidental '{' inside
                    // the binary header. Keep searching from the next byte.
                }
            }

            searchFrom = start + 1;
        }
    }

    private static int? FindBalancedEnd(byte[] bytes, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < bytes.Length; i++)
        {
            var b = (char)bytes[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (b == '\\')
                    escaped = true;
                else if (b == '"')
                    inString = false;
                continue;
            }

            switch (b)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }

        return null;
    }

    private async Task<JsonDocument> FindSingleEnvelopeForTenantAsync<TEvent>(Guid tenantId)
    {
        var candidates = await FindEnvelopesAsync<TEvent>();
        var matches = candidates
            .Where(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() == tenantId.ToString())
            .ToList();
        matches.Should().HaveCount(1,
            $"exactly one {typeof(TEvent).Name} envelope for tenant {tenantId} was expected in the outbox");
        return matches[0];
    }

    private async Task AssertNoEnvelopeAsync<TEvent>(Guid tenantId)
    {
        var candidates = await FindEnvelopesAsync<TEvent>();
        candidates.Any(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() == tenantId.ToString())
            .Should().BeFalse($"no {typeof(TEvent).Name} envelope should exist for tenant {tenantId}");
    }

    /// <summary>Case-insensitive property lookup: Wolverine's default JSON serializer's casing is an implementation detail this test must not depend on.</summary>
    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        }
        return null;
    }

    // ---- Tests: Login -------------------------------------------------------

    [Fact]
    public async Task Successful_login_publishes_UserLoggedIn_with_the_session_id_and_user_as_aggregate()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(() => ExecuteLoginAsync(host, LoginAs(slug, email), tenantId));
            result.IsSuccess.Should().BeTrue();

            var envelope = await FindSingleEnvelopeForTenantAsync<UserLoggedIn>(tenantId);
            var root = envelope.RootElement;
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(userId.ToString());
            GetProperty(root, "AggregateType")!.Value.GetString().Should().Be("User");
            GetProperty(root, "SessionId")!.Value.GetString().Should().NotBeNullOrEmpty();
            GetProperty(root, "ActorType")!.Value.GetString().Should().Be("User");
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Login_for_an_unknown_email_publishes_LoginFailed_with_a_null_user_id()
    {
        var tenantId = await SeedTenantAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(() => ExecuteLoginAsync(host, LoginAs(slug, "no-such-user@ihostpro.com"), tenantId));
            result.IsFailure.Should().BeTrue();

            var envelope = await FindSingleEnvelopeForTenantAsync<LoginFailed>(tenantId);
            var root = envelope.RootElement;
            GetProperty(root, "UserId")!.Value.ValueKind.Should().Be(JsonValueKind.Null);
            GetProperty(root, "ReasonCode")!.Value.GetString().Should().Be(LoginFailedReasonCodes.UserNotFound);
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(tenantId.ToString());
            GetProperty(root, "AggregateType")!.Value.GetString().Should().Be("Tenant");
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Login_for_a_blocked_user_publishes_LoginFailed_with_user_blocked_reason()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId, blocked: true);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            await WithBrokerPausedAsync(() => ExecuteLoginAsync(host, LoginAs(slug, email), tenantId));

            var envelope = await FindSingleEnvelopeForTenantAsync<LoginFailed>(tenantId);
            var root = envelope.RootElement;
            GetProperty(root, "UserId")!.Value.GetString().Should().Be(userId.ToString());
            GetProperty(root, "ReasonCode")!.Value.GetString().Should().Be(LoginFailedReasonCodes.UserBlocked);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Login_against_an_already_locked_account_publishes_LoginFailed_with_account_locked_reason()
    {
        const int threshold = 2;
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync(maxFailedAccessAttempts: threshold);
        try
        {
            await WithBrokerPausedAsync(async () =>
            {
                for (var i = 0; i < threshold; i++)
                    await ExecuteLoginAsync(host, LoginAs(slug, email, "wrong-password"), tenantId);

                // The account is now locked — a further attempt, even with the
                // correct password, must publish LoginFailed(account_locked),
                // never a second AccountLockedOut (that event represents only
                // the specific attempt that newly triggers the lockout).
                await ExecuteLoginAsync(host, LoginAs(slug, email), tenantId);
                return true;
            });

            var candidates = await FindEnvelopesAsync<LoginFailed>();
            var accountLockedEnvelopes = candidates
                .Where(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() == tenantId.ToString()
                    && GetProperty(doc.RootElement, "ReasonCode")?.GetString() == LoginFailedReasonCodes.AccountLocked)
                .ToList();
            accountLockedEnvelopes.Should().HaveCount(1);

            var accountLockedOutEnvelopes = await FindEnvelopesAsync<AccountLockedOut>();
            accountLockedOutEnvelopes.Count(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() == tenantId.ToString())
                .Should().Be(1); // exactly the one from the triggering attempt above, not duplicated
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Wrong_password_not_reaching_the_threshold_publishes_LoginFailed_with_invalid_password_reason_and_no_lockout_event()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync(maxFailedAccessAttempts: 10);
        try
        {
            await WithBrokerPausedAsync(() => ExecuteLoginAsync(host, LoginAs(slug, email, "wrong-password"), tenantId));

            var envelope = await FindSingleEnvelopeForTenantAsync<LoginFailed>(tenantId);
            GetProperty(envelope.RootElement, "ReasonCode")!.Value.GetString().Should().Be(LoginFailedReasonCodes.InvalidPassword);

            await AssertNoEnvelopeAsync<AccountLockedOut>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task The_attempt_that_triggers_lockout_publishes_LoginFailed_and_AccountLockedOut_exactly_once_each_with_a_future_lockout_end()
    {
        const int threshold = 3;
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync(maxFailedAccessAttempts: threshold);
        try
        {
            await WithBrokerPausedAsync(async () =>
            {
                for (var i = 0; i < threshold; i++)
                    await ExecuteLoginAsync(host, LoginAs(slug, email, "wrong-password"), tenantId);
                return true;
            });

            var loginFailedEnvelopes = (await FindEnvelopesAsync<LoginFailed>())
                .Where(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() == tenantId.ToString())
                .ToList();
            loginFailedEnvelopes.Should().HaveCount(threshold); // one per attempt, all invalid_password

            var lockedOutEnvelope = await FindSingleEnvelopeForTenantAsync<AccountLockedOut>(tenantId);
            var root = lockedOutEnvelope.RootElement;
            // AccountLockedOut has no UserId property — AggregateId carries it (Documento 07 §13.1).
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(userId.ToString());
            GetProperty(root, "ReasonCode")!.Value.GetString().Should().Be(AccountLockedOutReasonCodes.MaxFailedAttempts);
            var lockoutEnd = GetProperty(root, "LockoutEnd")!.Value.GetDateTimeOffset();
            lockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: Logout ------------------------------------------------------

    [Fact]
    public async Task Successful_logout_publishes_UserLoggedOut_and_SessionRevoked_with_logout_requested_reason()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(() => ExecuteLogoutAsync(host, new LogoutCommand(tenantId, userId, sessionId)));
            result.IsSuccess.Should().BeTrue();

            var loggedOut = await FindSingleEnvelopeForTenantAsync<UserLoggedOut>(tenantId);
            GetProperty(loggedOut.RootElement, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(loggedOut.RootElement, "AggregateId")!.Value.GetString().Should().Be(userId.ToString());

            var revoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(revoked.RootElement, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(revoked.RootElement, "ReasonCode")!.Value.GetString().Should().Be(SessionRevokedReasonCodes.LogoutRequested);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Repeated_logout_publishes_no_further_events()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var host = await BuildHostAsync();
        try
        {
            await ExecuteLogoutAsync(host, new LogoutCommand(tenantId, userId, sessionId));
            var envelopesAfterFirst = await CountOutgoingEnvelopesAsync();

            var second = await ExecuteLogoutAsync(host, new LogoutCommand(tenantId, userId, sessionId));

            second.IsSuccess.Should().BeTrue();
            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesAfterFirst); // no new envelope at all
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: Refresh Token reuse ------------------------------------------

    [Fact]
    public async Task Refresh_reuse_outside_the_grace_window_publishes_RefreshTokenReuseDetected_and_SessionRevoked_with_only_the_token_id()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddDays(-1), now.AddDays(29),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now.AddMinutes(-1))); // rotated well outside any grace window
        var host = await BuildHostAsync(graceWindow: TimeSpan.FromMilliseconds(1));
        try
        {
            var result = await WithBrokerPausedAsync(() => ExecuteRefreshAsync(
                host, new RefreshTokenCommand(presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")),
                tenantId));
            result.IsFailure.Should().BeTrue();

            var reuseDetected = await FindSingleEnvelopeForTenantAsync<RefreshTokenReuseDetected>(tenantId);
            var reuseRoot = reuseDetected.RootElement;
            GetProperty(reuseRoot, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(reuseRoot, "TokenId")!.Value.GetString().Should().Be(tokenId.ToString());
            reuseRoot.ToString().Should().NotContain(hash); // never the token hash
            reuseRoot.ToString().Should().NotContain(presented); // never the full presented token

            var revoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(revoked.RootElement, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(revoked.RootElement, "ReasonCode")!.Value.GetString().Should().Be(SessionRevokedReasonCodes.RefreshTokenReuseDetected);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Refresh_reuse_within_the_grace_window_publishes_no_reuse_or_revocation_event()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddDays(-1), now.AddDays(29),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now)); // rotated "just now"
        var host = await BuildHostAsync(graceWindow: TimeSpan.FromMinutes(1));
        try
        {
            var result = await ExecuteRefreshAsync(
                host, new RefreshTokenCommand(presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")),
                tenantId);
            result.IsFailure.Should().BeTrue(); // still rejected — just not classified as reuse

            await AssertNoEnvelopeAsync<RefreshTokenReuseDetected>(tenantId);
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: atomicity / rollback ------------------------------------

    [Fact]
    public async Task A_failure_after_UserLoggedIn_was_staged_rolls_back_the_domain_writes_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var executor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var handler = sp.GetRequiredService<LoginCommandHandler>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();

            var act = () => executor.ExecuteAsync<Result<AuthTokensResult>>(async () =>
            {
                var result = await handler.Handle(LoginAs(slug, email), CancellationToken.None);
                result.IsSuccess.Should().BeTrue(); // UserLoggedIn was staged by this point
                throw new InvalidOperationException("Simulated failure after Login staged its writes.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();

            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
            await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
            await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
            await SetPostgresTenantAsync(verifyDbContext, tenantId);
            (await verifyDbContext.Sessions.CountAsync(s => s.UserId == userId)).Should().Be(0);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: concurrency ------------------------------------------------

    [Fact]
    public async Task Concurrency_retry_on_refresh_reuse_publishes_the_reuse_events_exactly_once()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddDays(-1), now.AddDays(29),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now.AddMinutes(-1)));
        var host = await BuildHostAsync(graceWindow: TimeSpan.FromMilliseconds(1));
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            var refreshExecutor = new RefreshTokenExchangeExecutor(
                transactionExecutor, dbContext, collector, new SessionRevocationSignal(), new NullSessionRevocationCache());
            var attempt = 0;

            // Simulates the same DbUpdateConcurrencyException-triggered retry
            // already proven (Etapa 15A) to discard a reverted attempt's
            // staged events — here with the REAL RefreshTokenReuseDetected/
            // SessionRevoked events instead of the canary.
            await WithBrokerPausedAsync(() => refreshExecutor.ExecuteAsync(async () =>
            {
                attempt++;
                var result = await sp.GetRequiredService<RefreshTokenCommandHandler>().Handle(
                    new RefreshTokenCommand(presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")),
                    CancellationToken.None);

                if (attempt == 1)
                    throw new DbUpdateConcurrencyException("Simulated xmin conflict.");

                return result;
            }, CancellationToken.None));

            attempt.Should().Be(2);
            await FindSingleEnvelopeForTenantAsync<RefreshTokenReuseDetected>(tenantId); // exactly one — HasCount(1) inside the helper
            await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: broker outage / recovery -----------------------------------

    [Fact]
    public async Task Broker_unavailable_keeps_UserLoggedIn_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);

        // Dedicated, single-use RabbitMQ container — see
        // IdentityOutboxTransactionExecutorTests's equivalent doc comment for
        // why a container whose stop/start lifecycle is exercised by a test
        // must not be the class Fixture's shared one.
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteLoginAsync(host, LoginAs(slug, email), tenantId);
                result.IsSuccess.Should().BeTrue(); // commit succeeds regardless of broker reachability

                var pending = await FindSingleEnvelopeForTenantAsync<UserLoggedIn>(tenantId);
                pending.Should().NotBeNull(); // still durably persisted, not lost
            }
            finally
            {
                await StopGracefullyAsync(host);
            }

            await rabbitMqContainer.StartAsync();

            // Recovery is verified with a NEW host/connection — confirmed
            // empirically (Etapa 15A) that RabbitMQ.Client's
            // AutorecoveringConnection does not resume from a connection the
            // broker force-closed with reason "shutdown".
            var recoveryHost = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    var envelopes = await FindEnvelopesAsync<UserLoggedIn>();
                    return envelopes.All(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() != tenantId.ToString());
                }, TimeSpan.FromSeconds(30));
            }
            finally
            {
                await StopGracefullyAsync(recoveryHost);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Broker_unavailable_keeps_SessionRevoked_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteLogoutAsync(host, new LogoutCommand(tenantId, userId, sessionId));
                result.IsSuccess.Should().BeTrue();

                await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId); // still durably persisted
            }
            finally
            {
                await StopGracefullyAsync(host);
            }

            await rabbitMqContainer.StartAsync();

            var recoveryHost = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    var envelopes = await FindEnvelopesAsync<SessionRevoked>();
                    return envelopes.All(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() != tenantId.ToString());
                }, TimeSpan.FromSeconds(30));
            }
            finally
            {
                await StopGracefullyAsync(recoveryHost);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    // ---- Tests: routing configuration ----------------------------------------

    /// <summary>
    /// Confirms the fix from the RabbitMQ latency investigation (Incremento 2
    /// plan, homologação real): every route caps
    /// <c>Endpoint.FailuresBeforeCircuitBreaks</c> at 1 (Wolverine's own
    /// default is 3) and still uses the durable outbox. Resolved from
    /// <see cref="BuildHostAsync"/>'s already-running host (real RabbitMQ via
    /// the shared <see cref="Fixture"/>) rather than a throwaway
    /// never-started <see cref="WolverineOptions"/>: confirmed empirically
    /// that <c>UseDurableOutbox()</c>/<c>CircuitBreaking(...)</c> queue
    /// "delayed" configuration actions that Wolverine only applies to the
    /// real <see cref="Endpoint"/> instances while starting the host — a
    /// never-started options object still shows every endpoint at its
    /// construction-time defaults (e.g. <c>Mode == EndpointMode.Inline</c>)
    /// even after calling <c>UseDurableOutbox()</c>.
    /// </summary>
    [Theory]
    [InlineData("user_logged_in")]
    [InlineData("login_failed")]
    [InlineData("account_locked_out")]
    [InlineData("user_logged_out")]
    [InlineData("refresh_token_reuse_detected")]
    [InlineData("session_revoked")]
    public async Task Route_caps_FailuresBeforeCircuitBreaks_at_one_and_still_uses_the_durable_outbox(string routingKey)
    {
        var host = await BuildHostAsync();
        try
        {
            var options = host.Services.GetRequiredService<WolverineOptions>();
            var expectedUri = new Uri($"rabbitmq://exchange/identity-events/routing/{routingKey}");
            var matches = options.Transports.AllEndpoints().Where(e => e.Uri == expectedUri).ToList();

            matches.Should().HaveCount(1, $"exactly one endpoint should be configured for routing key '{routingKey}'");
            matches[0].FailuresBeforeCircuitBreaks.Should().Be(1,
                $"the opportunistic synchronous delivery attempt for '{routingKey}' must not retry inline more than once before deferring to the Durability Agent");
            matches[0].Mode.Should().Be(EndpointMode.Durable,
                $"'{routingKey}' must never fall back to Inline sending, which would discard messages during a broker outage instead of persisting them");
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    /// <summary>
    /// With the circuit breaker capped at a single attempt (previous test),
    /// a request that publishes two events in the same transaction (reuse
    /// detection: both <see cref="RefreshTokenReuseDetected"/> and
    /// <see cref="SessionRevoked"/>) while the broker is unreachable must
    /// complete close to normal PostgreSQL commit latency — not the
    /// multi-attempt, multi-minute delay observed before the fix (Incremento
    /// 2 plan, homologação real). This is a coarse regression guard (generous
    /// bound, not the objective &lt;1s target measured manually with repeated
    /// runs against the real broker — Fase 1 doc, Seção 11), since exact
    /// channel-open failure timing is not perfectly reproducible in an
    /// automated run; it exists to catch a future regression back to
    /// multiple synchronous retries, not to assert the tuned figure itself.
    /// Dedicated, single-use RabbitMQ container — see
    /// <see cref="Broker_unavailable_keeps_UserLoggedIn_pending_and_recovery_delivers_it"/>
    /// for why a container whose stop/start lifecycle is exercised by a test
    /// must not be the class Fixture's shared one.
    /// </summary>
    [Fact]
    public async Task Reuse_detection_with_broker_unreachable_completes_without_multiple_synchronous_retries()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var now = DateTimeOffset.UtcNow;
        var (presented, tokenId, hash) = BuildPresentedToken(tenantId);
        await SeedRefreshTokenAsync(
            tenantId, userId, sessionId, tokenId, hash, now.AddDays(-1), now.AddDays(29),
            mutate: t => t.MarkRotated(Guid.NewGuid(), now.AddMinutes(-1))); // rotated well outside any grace window

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(graceWindow: TimeSpan.FromMilliseconds(1), rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await ExecuteRefreshAsync(
                    host, new RefreshTokenCommand(presented, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari")),
                    tenantId);
                stopwatch.Stop();

                result.IsFailure.Should().BeTrue();
                stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
                    "a single-attempt circuit breaker must not let a broker outage add multiple synchronous retries to the request, even publishing two events");

                var pendingReuse = await FindSingleEnvelopeForTenantAsync<RefreshTokenReuseDetected>(tenantId);
                pendingReuse.Should().NotBeNull(); // still durably persisted, not lost
                var pendingRevoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
                pendingRevoked.Should().NotBeNull();
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<string> GetTenantSlugAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        return tenant.Slug.Value;
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }
}
