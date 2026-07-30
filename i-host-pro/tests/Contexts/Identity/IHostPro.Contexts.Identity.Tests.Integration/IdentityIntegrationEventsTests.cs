using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
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
        int maxFailedAccessAttempts = 5,
        TimeSpan? graceWindow = null,
        RabbitMqContainer? rabbitMqContainer = null,
        Action<IServiceCollection>? overrides = null)
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
        hostBuilder.Services.AddScoped<IRevokeOwnSessionExecutor, RevokeOwnSessionExecutor>();
        hostBuilder.Services.AddScoped<ICreateUserExecutor, CreateUserExecutor>();
        hostBuilder.Services.AddScoped<IAssignRoleExecutor, AssignRoleExecutor>();
        hostBuilder.Services.AddScoped<IRemoveRoleExecutor, RemoveRoleExecutor>();
        hostBuilder.Services.AddScoped<IBlockUserExecutor, BlockUserExecutor>();
        hostBuilder.Services.AddScoped<IUpdateUserExecutor, UpdateUserExecutor>();
        hostBuilder.Services.AddScoped<IChangeOwnPasswordExecutor, ChangeOwnPasswordExecutor>();
        hostBuilder.Services.AddScoped<ChangeOwnPasswordCommandHandler>();
        hostBuilder.Services.AddScoped<LoginCommandHandler>();
        hostBuilder.Services.AddScoped<LogoutCommandHandler>();
        hostBuilder.Services.AddScoped<RefreshTokenCommandHandler>();
        hostBuilder.Services.AddScoped<RevokeOwnSessionCommandHandler>();
        hostBuilder.Services.AddScoped<CreateUserCommandHandler>();
        hostBuilder.Services.AddScoped<AssignRoleCommandHandler>();
        hostBuilder.Services.AddScoped<RemoveRoleCommandHandler>();
        hostBuilder.Services.AddScoped<BlockUserCommandHandler>();
        hostBuilder.Services.AddScoped<UnblockUserCommandHandler>();
        hostBuilder.Services.AddScoped<UpdateUserCommandHandler>();

        overrides?.Invoke(hostBuilder.Services);

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
            RouteIdentityEvent<UserCreated>("user_created");
            RouteIdentityEvent<UserRoleAssigned>("user_role_assigned");
            RouteIdentityEvent<UserRoleRemoved>("user_role_removed");
            RouteIdentityEvent<UserBlocked>("user_blocked");
            RouteIdentityEvent<UserUnblocked>("user_unblocked");
            RouteIdentityEvent<UserUpdated>("user_updated");
            RouteIdentityEvent<PasswordChanged>("password_changed");
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

    private static async Task<Result> ExecuteRevokeOwnSessionAsync(
        IHost root, RevokeOwnSessionCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IRevokeOwnSessionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RevokeOwnSessionCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result<UserResult>> ExecuteCreateUserAsync(
        IHost root, CreateUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<ICreateUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<CreateUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteAssignRoleAsync(
        IHost root, AssignRoleCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IAssignRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteRemoveRoleAsync(
        IHost root, RemoveRoleCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IRemoveRoleExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteBlockUserAsync(
        IHost root, BlockUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IBlockUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteUnblockUserAsync(
        IHost root, UnblockUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        // No command-specific executor — mirrors ExecuteLoginAsync's shape
        // (see UnblockUserTenantAwareBehavior's doc comment for why).
        return await sp.GetRequiredService<IIdentityTransactionExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<UnblockUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result<UserResult>> ExecuteUpdateUserAsync(
        IHost root, UpdateUserCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IUpdateUserExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<UpdateUserCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    private static async Task<Result> ExecuteChangeOwnPasswordAsync(
        IHost root, ChangeOwnPasswordCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(command.TenantId);

        return await sp.GetRequiredService<IChangeOwnPasswordExecutor>().ExecuteAsync(
            () => sp.GetRequiredService<ChangeOwnPasswordCommandHandler>().Handle(command, cancellationToken).AsTask(),
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

    /// <summary>
    /// Assigns <paramref name="roleCode"/> to <paramref name="userId"/>
    /// directly (bypassing AssignRoleCommand) — used to set up the
    /// PRE-CONDITION state RemoveRole/AssignRole tests need (a role already
    /// held, or not), never to exercise the command under test itself.
    /// </summary>
    private async Task SeedUserRoleAsync(Guid tenantId, Guid userId, string roleCode, Guid assignedByUserId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        dbContext.UserRoles.Add(new UserRole(tenantId, userId, roleCode, DateTimeOffset.UtcNow, assignedByUserId));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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

    // ---- Tests: RevokeOwnSession (Incremento 3, Checkpoint 4) ------------------

    [Fact]
    public async Task Successful_RevokeOwnSession_publishes_exactly_one_SessionRevoked_with_user_requested_revocation_reason()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteRevokeOwnSessionAsync(host, new RevokeOwnSessionCommand(tenantId, userId, sessionId)));
            result.IsSuccess.Should().BeTrue();

            var revoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            var root = revoked.RootElement;
            GetProperty(root, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(userId.ToString());
            GetProperty(root, "AggregateType")!.Value.GetString().Should().Be("User");
            GetProperty(root, "ActorType")!.Value.GetString().Should().Be("User");
            GetProperty(root, "ActorId")!.Value.GetString().Should().Be(userId.ToString());
            GetProperty(root, "ReasonCode")!.Value.GetString().Should().Be(SessionRevokedReasonCodes.UserRequestedRevocation);

            // Never one event per refresh token, and no UserLoggedOut-style
            // companion event — RevokeOwnSession publishes only the one
            // SessionRevoked (Incremento 3, Checkpoint 4, Section 5).
            await AssertNoEnvelopeAsync<UserLoggedOut>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RevokeOwnSession_for_a_session_not_owned_by_the_caller_publishes_no_event()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(() => ExecuteRevokeOwnSessionAsync(
                host, new RevokeOwnSessionCommand(tenantId, userId, Guid.NewGuid())));
            result.IsFailure.Should().BeTrue();

            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: CreateUser (Incremento 3, Checkpoint 5) ------------------------

    [Fact]
    public async Task Successful_CreateUser_publishes_exactly_one_UserCreated_and_one_UserRoleAssigned_correctly_chained()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            var command = new CreateUserCommand(
                tenantId, actorId, "Test User", $"{Guid.NewGuid():N}@ihostpro.com",
                "Correct-Horse-Battery-Staple-42!", "ADMIN");

            var result = await WithBrokerPausedAsync(() => ExecuteCreateUserAsync(host, command));
            result.IsSuccess.Should().BeTrue();
            var newUserId = result.Value.Id;

            var userCreated = await FindSingleEnvelopeForTenantAsync<UserCreated>(tenantId);
            var createdRoot = userCreated.RootElement;
            GetProperty(createdRoot, "AggregateId")!.Value.GetString().Should().Be(newUserId.ToString());
            GetProperty(createdRoot, "AggregateType")!.Value.GetString().Should().Be("User");
            GetProperty(createdRoot, "ActorType")!.Value.GetString().Should().Be("User");
            GetProperty(createdRoot, "ActorId")!.Value.GetString().Should().Be(actorId.ToString());

            var userRoleAssigned = await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId);
            var assignedRoot = userRoleAssigned.RootElement;
            GetProperty(assignedRoot, "AggregateId")!.Value.GetString().Should().Be(newUserId.ToString());
            GetProperty(assignedRoot, "RoleCode")!.Value.GetString().Should().Be("ADMIN");
            GetProperty(assignedRoot, "ActorId")!.Value.GetString().Should().Be(actorId.ToString());
            GetProperty(assignedRoot, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(createdRoot, "EventId")!.Value.GetString());

            createdRoot.ToString().Should().NotContain("Correct-Horse-Battery-Staple-42!");
            assignedRoot.ToString().Should().NotContain("Correct-Horse-Battery-Staple-42!");
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task CreateUser_for_a_nonexistent_role_publishes_neither_event()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            var command = new CreateUserCommand(
                tenantId, actorId, "Test User", $"{Guid.NewGuid():N}@ihostpro.com",
                "Correct-Horse-Battery-Staple-42!", "NOT_A_REAL_ROLE");

            var result = await WithBrokerPausedAsync(() => ExecuteCreateUserAsync(host, command));
            result.IsFailure.Should().BeTrue();

            await AssertNoEnvelopeAsync<UserCreated>(tenantId);
            await AssertNoEnvelopeAsync<UserRoleAssigned>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: AssignRole / RemoveRole (Incremento 3, Checkpoint 6) --------------

    [Fact]
    public async Task Successful_AssignRole_publishes_exactly_one_UserRoleAssigned_with_correct_actor()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteAssignRoleAsync(host, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR")));
            result.IsSuccess.Should().BeTrue();

            var assigned = await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId);
            var root = assigned.RootElement;
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(targetUserId.ToString());
            GetProperty(root, "AggregateType")!.Value.GetString().Should().Be("User");
            GetProperty(root, "ActorType")!.Value.GetString().Should().Be("User");
            GetProperty(root, "ActorId")!.Value.GetString().Should().Be(actorId.ToString());
            GetProperty(root, "RoleCode")!.Value.GetString().Should().Be("OPERATOR");

            // No active session for the target — no cascade to trigger.
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task AssignRole_for_a_role_already_assigned_publishes_no_event()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteAssignRoleAsync(host, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR")));
            result.IsFailure.Should().BeTrue();

            await AssertNoEnvelopeAsync<UserRoleAssigned>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Successful_AssignRole_for_a_user_with_an_active_session_also_publishes_a_chained_SessionRevoked()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteAssignRoleAsync(host, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR")));
            result.IsSuccess.Should().BeTrue();

            var assigned = await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId);
            var revoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(revoked.RootElement, "SessionId")!.Value.GetString().Should().Be(sessionId.ToString());
            GetProperty(revoked.RootElement, "ReasonCode")!.Value.GetString().Should().Be(SessionRevokedReasonCodes.RolesChanged);
            GetProperty(revoked.RootElement, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(assigned.RootElement, "EventId")!.Value.GetString());
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Successful_RemoveRole_publishes_exactly_one_UserRoleRemoved_with_correct_actor()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteRemoveRoleAsync(host, new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER")));
            result.IsSuccess.Should().BeTrue();

            var removed = await FindSingleEnvelopeForTenantAsync<UserRoleRemoved>(tenantId);
            var root = removed.RootElement;
            GetProperty(root, "AggregateId")!.Value.GetString().Should().Be(targetUserId.ToString());
            GetProperty(root, "ActorId")!.Value.GetString().Should().Be(actorId.ToString());
            GetProperty(root, "RoleCode")!.Value.GetString().Should().Be("HOUSEKEEPER");
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RemoveRole_for_a_role_not_assigned_publishes_no_event()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteRemoveRoleAsync(host, new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER")));
            result.IsFailure.Should().BeTrue();

            await AssertNoEnvelopeAsync<UserRoleRemoved>(tenantId);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RemoveRole_of_the_tenants_last_active_Administrator_publishes_no_event()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId); // 2 roles, so the "last role" guard does not fire first
        var host = await BuildHostAsync();
        try
        {
            var result = await WithBrokerPausedAsync(
                () => ExecuteRemoveRoleAsync(host, new RemoveRoleCommand(tenantId, actorId, targetUserId, "ADMIN")));
            result.IsFailure.Should().BeTrue();

            await AssertNoEnvelopeAsync<UserRoleRemoved>(tenantId);
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

    [Fact]
    public async Task A_failure_after_RevokeOwnSession_staged_its_writes_rolls_back_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var executor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var handler = sp.GetRequiredService<RevokeOwnSessionCommandHandler>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();

            var act = () => executor.ExecuteAsync<Result>(async () =>
            {
                var result = await handler.Handle(new RevokeOwnSessionCommand(tenantId, userId, sessionId), CancellationToken.None);
                result.IsSuccess.Should().BeTrue(); // SessionRevoked was staged by this point
                throw new InvalidOperationException("Simulated failure after RevokeOwnSession staged its writes.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();

            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
            await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
            await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
            await SetPostgresTenantAsync(verifyDbContext, tenantId);
            var session = await verifyDbContext.Sessions.SingleAsync(s => s.Id == sessionId);
            session.Status.Should().Be(SessionStatus.Active); // never committed
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task A_failure_after_CreateUser_staged_its_writes_rolls_back_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var executor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var handler = sp.GetRequiredService<CreateUserCommandHandler>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();
            var command = new CreateUserCommand(
                tenantId, actorId, "Test User", $"{Guid.NewGuid():N}@ihostpro.com",
                "Correct-Horse-Battery-Staple-42!", "ADMIN");

            var act = () => executor.ExecuteAsync<Result<UserResult>>(async () =>
            {
                var result = await handler.Handle(command, CancellationToken.None);
                result.IsSuccess.Should().BeTrue(); // UserCreated/UserRoleAssigned were staged by this point
                throw new InvalidOperationException("Simulated failure after CreateUser staged its writes.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();

            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task A_failure_after_AssignRole_staged_its_writes_rolls_back_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var executor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var handler = sp.GetRequiredService<AssignRoleCommandHandler>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();
            var command = new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR");

            var act = () => executor.ExecuteAsync<Result>(async () =>
            {
                var result = await handler.Handle(command, CancellationToken.None);
                result.IsSuccess.Should().BeTrue(); // UserRoleAssigned was staged by this point
                throw new InvalidOperationException("Simulated failure after AssignRole staged its writes.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();

            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
            await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
            await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
            await SetPostgresTenantAsync(verifyDbContext, tenantId);
            (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "OPERATOR")).Should().Be(0);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task A_failure_after_RemoveRole_staged_its_writes_rolls_back_and_leaves_no_envelope()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var executor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var handler = sp.GetRequiredService<RemoveRoleCommandHandler>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();
            var command = new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER");

            var act = () => executor.ExecuteAsync<Result>(async () =>
            {
                var result = await handler.Handle(command, CancellationToken.None);
                result.IsSuccess.Should().BeTrue(); // UserRoleRemoved was staged by this point
                throw new InvalidOperationException("Simulated failure after RemoveRole staged its writes.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();

            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
            await using var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId);
            await using var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync();
            await SetPostgresTenantAsync(verifyDbContext, tenantId);
            (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "HOUSEKEEPER")).Should().Be(1); // never removed
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

    // ---- Tests: AssignRole / RemoveRole retry safety (Checkpoint 6 review) --------

    /// <summary>
    /// Records every <see cref="MarkRevokedAsync"/> call instead of touching
    /// Redis — precise, deterministic proof of "exactly one signal per
    /// session, only from the winning attempt" without needing a real Redis
    /// container in this file's Fixture.
    /// </summary>
    private sealed class RecordingSessionRevocationCache : ISessionRevocationCache
    {
        public List<(Guid TenantId, Guid SessionId)> MarkedCalls { get; } = [];

        public Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
        {
            MarkedCalls.Add((tenantId, sessionId));
            return Task.CompletedTask;
        }

        public Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by the retry-safety tests.");
    }

    [Fact]
    public async Task AssignRole_retry_after_a_reverted_concurrency_conflict_confirms_once_on_the_winning_attempt()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new AssignRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR");

            var result = await WithBrokerPausedAsync(() => executor.ExecuteAsync(async () =>
            {
                attempt++;
                var handlerResult = await sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, CancellationToken.None);

                if (attempt == 1)
                    throw new DbUpdateConcurrencyException("Simulated xmin conflict.");

                return handlerResult;
            }, CancellationToken.None));

            attempt.Should().Be(2); // one reverted attempt, one winning attempt — never more
            result.IsSuccess.Should().BeTrue();

            var roleAssigned = await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId);
            var sessionRevoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(sessionRevoked.RootElement, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(roleAssigned.RootElement, "EventId")!.Value.GetString(),
                    "CausationId must point only at the WINNING attempt's own primary event, never a discarded one");

            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(1);
                (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "OPERATOR")).Should().Be(1);
            }

            cache.MarkedCalls.Should().Equal((tenantId, sessionId)); // exactly one signal, never one per attempt

            // No leftover/duplicate tracked entity from the reverted attempt —
            // if ChangeTracker.Clear() had not run between attempts, the
            // second attempt's Add() of a UserRole with the same (UserId,
            // RoleCode) key as the still-tracked first attempt's would have
            // thrown InvalidOperationException before ever reaching here.
            dbContext.ChangeTracker.Entries<UserRole>().Count(e => e.Entity.RoleCode == "OPERATOR").Should().Be(1);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task AssignRole_when_every_attempt_hits_a_concurrency_conflict_the_exception_propagates_and_nothing_is_left_behind()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new AssignRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR");

            var act = () => executor.ExecuteAsync(async () =>
            {
                attempt++;
                await sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, CancellationToken.None);
                throw new DbUpdateConcurrencyException("Simulated persistent xmin conflict.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

            attempt.Should().Be(3); // exactly MaxConcurrencyRetryAttempts, never more
            await AssertNoEnvelopeAsync<UserRoleAssigned>(tenantId);
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
                (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "OPERATOR")).Should().Be(0);
            }
            cache.MarkedCalls.Should().BeEmpty();
            // The exact invariant the Checkpoint 6 review flagged: even on the
            // final, non-retried failure, cleanup must have run — collector
            // and signal must both end up empty, not just on retry-eligible
            // attempts.
            collector.Drain().Should().BeEmpty();
            signal.Drain().Should().BeEmpty();
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task AssignRole_RoleAlreadyAssigned_rejection_completes_on_the_first_attempt_without_retrying()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new AssignRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR"); // already assigned

            var result = await executor.ExecuteAsync(async () =>
            {
                attempt++;
                return await sp.GetRequiredService<AssignRoleCommandHandler>().Handle(command, CancellationToken.None);
            }, CancellationToken.None);

            // A Result.Failure is a normal return value, never a thrown
            // DbUpdateConcurrencyException — the retry catch clause cannot
            // and does not match it.
            attempt.Should().Be(1);
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(IdentityErrorCodes.RoleAlreadyAssigned);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RemoveRole_retry_after_a_reverted_concurrency_conflict_confirms_once_on_the_winning_attempt()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        var (otherAdminId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, otherAdminId, "ADMIN", actorId); // so removal is legal
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new RemoveRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            // Removing ADMIN specifically forces ILastAdministratorGuard to
            // run on EVERY attempt, including the retried one — proving the
            // guard (and every other read) genuinely re-executes from
            // scratch, never reusing a stale outcome from the reverted
            // attempt.
            var command = new RemoveRoleCommand(tenantId, actorId, targetUserId, "ADMIN");

            var result = await WithBrokerPausedAsync(() => executor.ExecuteAsync(async () =>
            {
                attempt++;
                var handlerResult = await sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, CancellationToken.None);

                if (attempt == 1)
                    throw new DbUpdateConcurrencyException("Simulated xmin conflict.");

                return handlerResult;
            }, CancellationToken.None));

            attempt.Should().Be(2);
            result.IsSuccess.Should().BeTrue();

            var roleRemoved = await FindSingleEnvelopeForTenantAsync<UserRoleRemoved>(tenantId);
            var sessionRevoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(sessionRevoked.RootElement, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(roleRemoved.RootElement, "EventId")!.Value.GetString());

            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(1);
                (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "ADMIN")).Should().Be(0);
            }

            cache.MarkedCalls.Should().Equal((tenantId, sessionId));
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RemoveRole_when_every_attempt_hits_a_concurrency_conflict_the_exception_propagates_and_nothing_is_left_behind()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new RemoveRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER");

            var act = () => executor.ExecuteAsync(async () =>
            {
                attempt++;
                await sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, CancellationToken.None);
                throw new DbUpdateConcurrencyException("Simulated persistent xmin conflict.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

            attempt.Should().Be(3);
            await AssertNoEnvelopeAsync<UserRoleRemoved>(tenantId);
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
                (await verifyDbContext.UserRoles.CountAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "HOUSEKEEPER")).Should().Be(1); // never removed
            }
            cache.MarkedCalls.Should().BeEmpty();
            collector.Drain().Should().BeEmpty();
            signal.Drain().Should().BeEmpty();
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RemoveRole_LastActiveAdministrator_rejection_completes_on_the_first_attempt_without_retrying()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed:
            // AssignRoleCommandHandler's IUserSessionRevoker dependency (via
            // UserSessionRevoker) also resolves ISessionRevocationSignal from
            // this scope — a standalone `new SessionRevocationSignal()` here
            // would be a second, disconnected instance the handler never
            // writes to, silently making every signal-related assertion below
            // vacuous (empty for the wrong reason).
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new RemoveRoleExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new RemoveRoleCommand(tenantId, actorId, targetUserId, "ADMIN"); // sole active admin

            var result = await executor.ExecuteAsync(async () =>
            {
                attempt++;
                return await sp.GetRequiredService<RemoveRoleCommandHandler>().Handle(command, CancellationToken.None);
            }, CancellationToken.None);

            attempt.Should().Be(1);
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: BlockUser retry safety (Checkpoint 7 review) ----------------------

    [Fact]
    public async Task BlockUser_retry_after_a_reverted_concurrency_conflict_confirms_once_on_the_winning_attempt()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        var (otherAdminId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, otherAdminId, "ADMIN", actorId); // so the block is legal
        var sessionId = await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            // Resolved from the SAME DI scope, not manually constructed — see
            // the identical note on the AssignRole/RemoveRole retry-safety
            // tests above.
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new BlockUserExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            // Blocking an ADMIN specifically forces ILastAdministratorGuard to
            // run on EVERY attempt, including the retried one — proving the
            // guard (and every other read) genuinely re-executes from
            // scratch, never reusing a stale outcome from the reverted
            // attempt, exactly as already proven for RemoveRole.
            var command = new BlockUserCommand(tenantId, actorId, targetUserId);

            var result = await WithBrokerPausedAsync(() => executor.ExecuteAsync(async () =>
            {
                attempt++;
                var handlerResult = await sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, CancellationToken.None);

                if (attempt == 1)
                    throw new DbUpdateConcurrencyException("Simulated xmin conflict.");

                return handlerResult;
            }, CancellationToken.None));

            attempt.Should().Be(2); // one reverted attempt, one winning attempt — never more
            // If ChangeTracker.Clear() had not run between attempts, the
            // second attempt's GetByIdAsync would resolve the SAME tracked
            // User instance the reverted first attempt already called
            // Block() on — whose Status is already Blocked — and the
            // handler's own UserAlreadyBlocked guard would reject it with a
            // Result.Failure instead of succeeding. Success here is direct
            // proof the entity was reloaded fresh, not reused stale.
            result.IsSuccess.Should().BeTrue();

            var userBlocked = await FindSingleEnvelopeForTenantAsync<UserBlocked>(tenantId);
            var sessionRevoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(sessionRevoked.RootElement, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(userBlocked.RootElement, "EventId")!.Value.GetString(),
                    "CausationId must point only at the WINNING attempt's own primary event, never a discarded one");

            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(1);
                (await verifyDbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
                    .Should().Be(UserStatus.Blocked);
            }

            cache.MarkedCalls.Should().Equal((tenantId, sessionId)); // exactly one signal, never one per attempt
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task BlockUser_when_every_attempt_hits_a_concurrency_conflict_the_exception_propagates_and_nothing_is_left_behind()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedSessionAsync(tenantId, targetUserId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new BlockUserExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new BlockUserCommand(tenantId, actorId, targetUserId);

            var act = () => executor.ExecuteAsync(async () =>
            {
                attempt++;
                await sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, CancellationToken.None);
                throw new DbUpdateConcurrencyException("Simulated persistent xmin conflict.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

            attempt.Should().Be(3); // exactly MaxConcurrencyRetryAttempts, never more
            await AssertNoEnvelopeAsync<UserBlocked>(tenantId);
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);
            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == targetUserId)).Should().Be(0);
                (await verifyDbContext.Users.Where(u => u.Id == targetUserId).Select(u => u.Status).SingleAsync())
                    .Should().Be(UserStatus.Active); // never persisted as Blocked
            }
            cache.MarkedCalls.Should().BeEmpty();
            // The exact invariant the Checkpoint 6 review flagged, applied to
            // BlockUserExecutor from the start: even on the final,
            // non-retried failure, cleanup must have run — collector and
            // signal must both end up empty, not just on retry-eligible
            // attempts.
            collector.Drain().Should().BeEmpty();
            signal.Drain().Should().BeEmpty();
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task BlockUser_UserAlreadyBlocked_rejection_completes_on_the_first_attempt_without_retrying()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId, blocked: true);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new BlockUserExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new BlockUserCommand(tenantId, actorId, targetUserId); // already blocked

            var result = await executor.ExecuteAsync(async () =>
            {
                attempt++;
                return await sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, CancellationToken.None);
            }, CancellationToken.None);

            // A Result.Failure is a normal return value, never a thrown
            // DbUpdateConcurrencyException — the retry catch clause cannot
            // and does not match it.
            attempt.Should().Be(1);
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(IdentityErrorCodes.UserAlreadyBlocked);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task BlockUser_LastActiveAdministrator_rejection_completes_on_the_first_attempt_without_retrying()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId); // sole active admin
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new BlockUserExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new BlockUserCommand(tenantId, actorId, targetUserId);

            var result = await executor.ExecuteAsync(async () =>
            {
                attempt++;
                return await sp.GetRequiredService<BlockUserCommandHandler>().Handle(command, CancellationToken.None);
            }, CancellationToken.None);

            attempt.Should().Be(1);
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(IdentityErrorCodes.LastActiveAdministrator);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- Tests: ChangeOwnPassword concurrency (Checkpoint 9 follow-up review) ----

    /// <summary>
    /// Forces genuine PostgreSQL transaction overlap between two concurrent
    /// password changes of the SAME user — unlike
    /// <c>UpdateUserCommandHandlerTests.BarrierSecurityAuditWriter</c> (which
    /// only synchronizes, since that test never asserts on the audit row),
    /// this one ALSO stages the entry exactly like the real
    /// <see cref="SecurityAuditWriter"/> does before waiting at the barrier —
    /// required here because this test's own audit-count assertion
    /// (Checkpoint 9 follow-up review, Section 3: "somente uma auditoria
    /// PasswordChanged") would otherwise always see zero rows, having swapped
    /// out the real writer entirely.
    /// </summary>
    private sealed class BarrierSecurityAuditWriter : ISecurityAuditWriter
    {
        private readonly IdentityDbContext _dbContext;
        private readonly Barrier _barrier;

        public BarrierSecurityAuditWriter(IdentityDbContext dbContext, Barrier barrier)
        {
            _dbContext = dbContext;
            _barrier = barrier;
        }

        public void Record(SecurityAuditEntry entry)
        {
            _dbContext.SecurityAuditLog.Add(entry);
            _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Mandatory regression from the Checkpoint 9 follow-up review, Section 3:
    /// two GENUINELY concurrent <see cref="ChangeOwnPasswordCommand"/>
    /// executions on the SAME user, forced to overlap via
    /// <see cref="BarrierSecurityAuditWriter"/> (both read the same row before
    /// either commits — a bare <c>Task.WhenAll</c> alone does not guarantee
    /// this, confirmed empirically by <c>UpdateUserCommandHandlerTests</c>'s
    /// own equivalent test). Verifies every outcome the review demanded in one
    /// pass: exactly one confirms, the other returns
    /// <see cref="IdentityErrorCodes.UserConcurrencyConflict"/> with no retry
    /// (the executor has none), only the winning password authenticates
    /// afterward, exactly one <see cref="SecurityAuditEventType.PasswordChangedBySelf"/>
    /// audit entry, exactly one <see cref="PasswordChanged"/> envelope (the
    /// winner's — a second, losing envelope would make
    /// <see cref="FindSingleEnvelopeForTenantAsync{TEvent}"/> fail with count
    /// 2), and exactly one <see cref="SessionRevoked"/> envelope chained to it.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_own_password_changes_of_the_same_user_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        await SeedSessionAsync(tenantId, userId);
        using var barrier = new Barrier(2);
        var hostA = await BuildHostAsync(
            overrides: sc => sc.AddScoped<ISecurityAuditWriter>(
                sp => new BarrierSecurityAuditWriter(sp.GetRequiredService<IdentityDbContext>(), barrier)));
        var hostB = await BuildHostAsync(
            overrides: sc => sc.AddScoped<ISecurityAuditWriter>(
                sp => new BarrierSecurityAuditWriter(sp.GetRequiredService<IdentityDbContext>(), barrier)));
        try
        {
            var results = await WithBrokerPausedAsync(async () =>
            {
                var taskA = ExecuteChangeOwnPasswordAsync(hostA, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, "Password-A-42!"));
                var taskB = ExecuteChangeOwnPasswordAsync(hostB, new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, "Password-B-42!"));
                return await Task.WhenAll(taskA, taskB);
            });

            results.Count(r => r.IsSuccess).Should().Be(1);
            var failure = results.Single(r => r.IsFailure);
            failure.Error.Code.Should().Be(IdentityErrorCodes.UserConcurrencyConflict);

            // Only the winning password authenticates afterward — the other
            // attempt's password never took effect, and the original password
            // is gone either way.
            var loginWithA = await ExecuteLoginAsync(hostA, LoginAs(slug, email, "Password-A-42!"), tenantId);
            var loginWithB = await ExecuteLoginAsync(hostA, LoginAs(slug, email, "Password-B-42!"), tenantId);
            var loginWithOld = await ExecuteLoginAsync(hostA, LoginAs(slug, email, KnownPassword), tenantId);
            new[] { loginWithA.IsSuccess, loginWithB.IsSuccess }.Count(success => success).Should().Be(1);
            loginWithOld.IsSuccess.Should().BeFalse();

            await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await SetPostgresTenantAsync(dbContext, tenantId);
            (await dbContext.SecurityAuditLog.CountAsync(
                e => e.UserId == userId && e.EventType == SecurityAuditEventType.PasswordChangedBySelf))
                .Should().Be(1);

            var passwordChanged = await FindSingleEnvelopeForTenantAsync<PasswordChanged>(tenantId);
            var sessionRevoked = await FindSingleEnvelopeForTenantAsync<SessionRevoked>(tenantId);
            GetProperty(sessionRevoked.RootElement, "CausationId")!.Value.GetString()
                .Should().Be(GetProperty(passwordChanged.RootElement, "EventId")!.Value.GetString(),
                    "CausationId must point only at the WINNING attempt's own PasswordChanged, never a discarded one");
        }
        finally
        {
            await StopGracefullyAsync(hostA);
            await StopGracefullyAsync(hostB);
        }
    }

    /// <summary>
    /// Mandatory regression from the Checkpoint 9 follow-up review, Section 3
    /// ("cenário em que a concorrência é injetada antes do commit"): unlike
    /// the genuine two-host race above (which cannot inspect either
    /// participant's collector/signal/ChangeTracker after the fact — each
    /// uses its own disposed scope), this test keeps a handle to a single
    /// scope's <see cref="IdentityDbContext"/>/<see cref="IIntegrationEventCollector"/>/
    /// <see cref="ISessionRevocationSignal"/> and forces the SAME
    /// <see cref="DbUpdateConcurrencyException"/> <see cref="ChangeOwnPasswordExecutor"/>
    /// would see from a real <c>xmin</c> race — mirrors
    /// <see cref="AssignRole_when_every_attempt_hits_a_concurrency_conflict_the_exception_propagates_and_nothing_is_left_behind"/>'s
    /// technique, adapted to this executor's shape: no retry loop (Section 8 of
    /// the Checkpoint 9 decision), so the conflict is translated to a returned
    /// <see cref="Result.Failure"/> on the FIRST and only attempt, never
    /// re-thrown.
    /// </summary>
    [Fact]
    public async Task ChangeOwnPassword_a_concurrency_conflict_injected_before_commit_translates_once_without_retry_and_writes_no_Redis_signal()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dbContext = sp.GetRequiredService<IdentityDbContext>();
            var transactionExecutor = sp.GetRequiredService<IIdentityTransactionExecutor>();
            var collector = sp.GetRequiredService<IIntegrationEventCollector>();
            var signal = sp.GetRequiredService<ISessionRevocationSignal>();
            var cache = new RecordingSessionRevocationCache();
            var executor = new ChangeOwnPasswordExecutor(transactionExecutor, dbContext, collector, signal, cache);
            var attempt = 0;
            var command = new ChangeOwnPasswordCommand(tenantId, userId, KnownPassword, "New-Password-42!");

            var result = await executor.ExecuteAsync(async () =>
            {
                attempt++;
                await sp.GetRequiredService<ChangeOwnPasswordCommandHandler>().Handle(command, CancellationToken.None);
                throw new DbUpdateConcurrencyException("Simulated xmin conflict.");
            }, CancellationToken.None);

            attempt.Should().Be(1); // no retry, unlike AssignRole/RemoveRole/Block
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(IdentityErrorCodes.UserConcurrencyConflict);

            await AssertNoEnvelopeAsync<PasswordChanged>(tenantId);
            await AssertNoEnvelopeAsync<SessionRevoked>(tenantId);

            await using (var verifyDbContext = CreateMigratorDbContextWithTenant(tenantId))
            await using (var verifyTransaction = await verifyDbContext.Database.BeginTransactionAsync())
            {
                await SetPostgresTenantAsync(verifyDbContext, tenantId);
                (await verifyDbContext.SecurityAuditLog.CountAsync(e => e.UserId == userId)).Should().Be(0);
                (await verifyDbContext.Sessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync())
                    .Should().Be(SessionStatus.Active); // untouched — the whole transaction rolled back
            }

            // The exact invariants the Checkpoint 9 follow-up review flagged:
            // no Redis write from the conflicted attempt, and collector/signal/
            // ChangeTracker all end up empty rather than leaking into the next
            // operation on this scope.
            cache.MarkedCalls.Should().BeEmpty();
            collector.Drain().Should().BeEmpty();
            signal.Drain().Should().BeEmpty();
            dbContext.ChangeTracker.Entries().Should().BeEmpty();
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

    [Fact]
    public async Task Broker_unavailable_keeps_RevokeOwnSession_SessionRevoked_pending_and_recovery_delivers_it()
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
                var result = await ExecuteRevokeOwnSessionAsync(host, new RevokeOwnSessionCommand(tenantId, userId, sessionId));
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

    [Fact]
    public async Task Broker_unavailable_keeps_both_CreateUser_events_pending_and_recovery_delivers_them()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var command = new CreateUserCommand(
                    tenantId, actorId, "Test User", $"{Guid.NewGuid():N}@ihostpro.com",
                    "Correct-Horse-Battery-Staple-42!", "ADMIN");
                var result = await ExecuteCreateUserAsync(host, command);
                result.IsSuccess.Should().BeTrue(); // commit succeeds regardless of broker reachability

                await FindSingleEnvelopeForTenantAsync<UserCreated>(tenantId); // still durably persisted
                await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId);
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
                    var createdEnvelopes = await FindEnvelopesAsync<UserCreated>();
                    var roleAssignedEnvelopes = await FindEnvelopesAsync<UserRoleAssigned>();
                    return createdEnvelopes.All(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() != tenantId.ToString())
                        && roleAssignedEnvelopes.All(doc => GetProperty(doc.RootElement, "TenantId")?.GetString() != tenantId.ToString());
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
    public async Task Broker_unavailable_keeps_AssignRole_UserRoleAssigned_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteAssignRoleAsync(
                    host, new AssignRoleCommand(tenantId, actorId, targetUserId, "OPERATOR"));
                result.IsSuccess.Should().BeTrue(); // commit succeeds regardless of broker reachability

                await FindSingleEnvelopeForTenantAsync<UserRoleAssigned>(tenantId); // still durably persisted
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
                    var envelopes = await FindEnvelopesAsync<UserRoleAssigned>();
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
    public async Task Broker_unavailable_keeps_RemoveRole_UserRoleRemoved_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteRemoveRoleAsync(
                    host, new RemoveRoleCommand(tenantId, actorId, targetUserId, "HOUSEKEEPER"));
                result.IsSuccess.Should().BeTrue();

                await FindSingleEnvelopeForTenantAsync<UserRoleRemoved>(tenantId); // still durably persisted
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
                    var envelopes = await FindEnvelopesAsync<UserRoleRemoved>();
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
    public async Task Broker_unavailable_keeps_BlockUser_UserBlocked_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteBlockUserAsync(host, new BlockUserCommand(tenantId, actorId, targetUserId));
                result.IsSuccess.Should().BeTrue();

                await FindSingleEnvelopeForTenantAsync<UserBlocked>(tenantId); // still durably persisted
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
                    var envelopes = await FindEnvelopesAsync<UserBlocked>();
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
    public async Task Broker_unavailable_keeps_UnblockUser_UserUnblocked_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId, blocked: true);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteUnblockUserAsync(host, new UnblockUserCommand(tenantId, actorId, targetUserId));
                result.IsSuccess.Should().BeTrue();

                await FindSingleEnvelopeForTenantAsync<UserUnblocked>(tenantId); // still durably persisted
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
                    var envelopes = await FindEnvelopesAsync<UserUnblocked>();
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
    public async Task Broker_unavailable_keeps_UpdateUser_UserUpdated_pending_and_recovery_delivers_it()
    {
        var tenantId = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);

        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer: rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();
                var result = await ExecuteUpdateUserAsync(
                    host, new UpdateUserCommand(tenantId, actorId, targetUserId, "New Name", null));
                result.IsSuccess.Should().BeTrue();

                await FindSingleEnvelopeForTenantAsync<UserUpdated>(tenantId); // still durably persisted
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
                    var envelopes = await FindEnvelopesAsync<UserUpdated>();
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
