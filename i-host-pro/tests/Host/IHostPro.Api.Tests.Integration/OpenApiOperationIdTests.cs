using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Preventive regression test for the OpenAPI operationId collision defect
/// (Fase 6, Checkpoint 6, gate §1) — see <c>Program.SwaggerOperationIdSelector</c>'s
/// own doc comment for the full defect history:
/// <c>ReservationsController.CancelReservation</c> and
/// <c>CleaningsController.CancelCleaning</c> both end in a route segment
/// literally named <c>cancel</c>, and Swashbuckle leaves <c>OperationId</c>
/// unset by default in this project, so NSwag's TypeScript generator
/// synthesized two identical fallback names ("cancel"/"cancel2") — silently
/// pointing Reservations' already-shipped "cancel" client method at the
/// Cleanings route instead.
///
/// This verifies against the REAL, fully-composed OpenAPI document produced
/// by <c>IHostPro.Api</c>'s own <c>Program.cs</c> — via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, the same established
/// technique <c>WolverineThreeStoreCompositionTests</c> uses to exercise the
/// real composition root, never a hand-maintained list of controllers/routes
/// that could itself drift from what Program.cs actually registers. All six
/// schemas (Identity/PropertyManagement/Reservations/Configuration/
/// Housekeeping/Platform) are provisioned via the real, unmodified
/// <c>IHostPro.MigrationRunner</c> executable — the same tool production
/// uses — rather than a hand-rolled per-context EF migration loop.
/// </summary>
public sealed class OpenApiOperationIdTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;

    private static readonly string[] EnvironmentKeys =
    [
        "ConnectionStrings__Identity", "ConnectionStrings__PropertyManagement", "ConnectionStrings__Reservations",
        "ConnectionStrings__Configuration", "ConnectionStrings__Housekeeping", "ConnectionStrings__Platform",
        "Identity__Jwt__Issuer", "Identity__Jwt__Audience", "Identity__Jwt__AccessTokenLifetime", "Identity__Jwt__ClockSkew",
        "Identity__Jwt__SigningKey__PrivateKeyPem",
        "Identity__AccountLockout__MaxFailedAccessAttempts", "Identity__AccountLockout__DefaultLockoutDuration", "Identity__AccountLockout__AllowedForNewUsers",
        "Identity__RefreshToken__Lifetime", "Identity__RefreshToken__SecretSizeBytes", "Identity__RefreshToken__ConcurrentRotationGraceWindow",
        "RabbitMq__Host", "RabbitMq__VirtualHost", "RabbitMq__Username", "RabbitMq__Password",
        "OpenTelemetry__OtlpEndpoint",
        "ASPNETCORE_ENVIRONMENT",
    ];

    public async Task InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_test")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();
        await _postgresContainer.StartAsync();

        // Program.cs's own RabbitMQ wiring (WolverineConfigurationExtensions.
        // UseIHostProRabbitMq) has no port override — always the default AMQP
        // port 5672 — same rationale as WolverineThreeStoreCompositionTests.
        // Fixture's identical binding. The host machine's own dev/homolog
        // RabbitMQ containers must be stopped before this test runs.
        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithPortBinding(5672, 5672)
            .Build();
        await _rabbitMqContainer.StartAsync();

        var adminConnectionString = _postgresContainer.GetConnectionString();
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
        _migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;

        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    /// <summary>
    /// Fase 6, Incremento 2A: <c>MyCleaningsController</c>'s self-service
    /// lifecycle actions (Start/StartInspection/Complete/WaitingMaterials/
    /// WaitingHelp) share the exact same last route segment as
    /// <c>CleaningsController</c>'s administrative actions — the identical
    /// collision class this file's own generic duplicate-operationId check
    /// already covers by construction. This constant + the loop in the test
    /// below is the same style of targeted, real-document verification
    /// already used for CancelReservation/CancelCleaning, extended rather
    /// than duplicated.
    /// </summary>
    private static readonly (string OperationId, string Path)[] SelfServiceCollisionPairs =
    [
        ("StartOwnCleaning", "/api/v1/my-cleanings/{cleaningId}/start"),
        ("StartOwnCleaningInspection", "/api/v1/my-cleanings/{cleaningId}/start-inspection"),
        ("CompleteOwnCleaning", "/api/v1/my-cleanings/{cleaningId}/complete"),
        ("MarkOwnCleaningWaitingMaterials", "/api/v1/my-cleanings/{cleaningId}/waiting-materials"),
        ("MarkOwnCleaningWaitingHelp", "/api/v1/my-cleanings/{cleaningId}/waiting-help"),
    ];

    [Fact]
    public async Task The_real_composed_OpenAPI_document_has_no_duplicate_operationId_and_CancelReservation_CancelCleaning_map_to_the_correct_routes()
    {
        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Identity"] = _appConnectionString,
            ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
            ["ConnectionStrings__Reservations"] = _appConnectionString,
            ["ConnectionStrings__Configuration"] = _appConnectionString,
            ["ConnectionStrings__Housekeeping"] = _appConnectionString,
            ["ConnectionStrings__Platform"] = _appConnectionString,
            ["Identity__Jwt__Issuer"] = "https://identity.ihostpro.test",
            ["Identity__Jwt__Audience"] = "ihostpro-api-test",
            ["Identity__Jwt__AccessTokenLifetime"] = "00:15:00",
            ["Identity__Jwt__ClockSkew"] = "00:01:00",
            ["Identity__Jwt__SigningKey__PrivateKeyPem"] = signingKeyPem,
            ["Identity__AccountLockout__MaxFailedAccessAttempts"] = "5",
            ["Identity__AccountLockout__DefaultLockoutDuration"] = "00:05:00",
            ["Identity__AccountLockout__AllowedForNewUsers"] = "true",
            ["Identity__RefreshToken__Lifetime"] = "30.00:00:00",
            ["Identity__RefreshToken__SecretSizeBytes"] = "32",
            ["Identity__RefreshToken__ConcurrentRotationGraceWindow"] = "00:00:10",
            ["RabbitMq__Host"] = _rabbitMqContainer.Hostname,
            ["RabbitMq__VirtualHost"] = "/",
            ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
            ["OpenTelemetry__OtlpEndpoint"] = "http://localhost:14319",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };

        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/swagger/v1/swagger.json");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var paths = document.RootElement.GetProperty("paths");

            var operationIds = new List<(string OperationId, string Path, string Verb)>();
            (string Path, string Verb)? cancelReservation = null;
            (string Path, string Verb)? cancelCleaning = null;

            foreach (var pathProperty in paths.EnumerateObject())
            {
                foreach (var verbProperty in pathProperty.Value.EnumerateObject())
                {
                    if (!verbProperty.Value.TryGetProperty("operationId", out var operationIdElement))
                        continue;

                    var operationId = operationIdElement.GetString()!;
                    operationIds.Add((operationId, pathProperty.Name, verbProperty.Name));

                    if (operationId == "CancelReservation")
                        cancelReservation = (pathProperty.Name, verbProperty.Name);
                    if (operationId == "CancelCleaning")
                        cancelCleaning = (pathProperty.Name, verbProperty.Name);
                }
            }

            var duplicates = operationIds
                .GroupBy(o => o.OperationId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToArray();

            duplicates.Should().BeEmpty(
                "no two operations in the real, fully-composed OpenAPI document may share an operationId " +
                $"(found: {string.Join(", ", duplicates.Select(g => $"{g.Key} x{g.Count()}"))})");

            cancelReservation.Should().Be(("/api/v1/reservations/{reservationId}/cancel", "post"),
                "CancelReservation must be assigned and map to the real Reservations cancel route");
            cancelCleaning.Should().Be(("/api/v1/cleanings/{cleaningId}/cancel", "post"),
                "CancelCleaning must be assigned and map to the real Cleanings cancel route");

            foreach (var (expectedOperationId, expectedPath) in SelfServiceCollisionPairs)
            {
                var match = operationIds.SingleOrDefault(o => o.OperationId == expectedOperationId);
                match.Should().NotBe(default,
                    $"{expectedOperationId} must be assigned to exactly one operation in the real OpenAPI document");
                match.Path.Should().Be(expectedPath, $"{expectedOperationId} must map to the real self-service route, not the administrative one");
                match.Verb.Should().Be("post");
            }
        }
        finally
        {
            foreach (var key in EnvironmentKeys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    /// Runs the actual built <c>IHostPro.MigrationRunner</c> executable as a
    /// real subprocess (never re-implemented in-test) against this test's own
    /// Postgres (migrator role) and RabbitMQ containers, for ALL SIX
    /// connection strings Program.cs's real composition needs
    /// (Identity/PropertyManagement/Reservations/Configuration/Housekeeping/
    /// Platform) — unlike <c>WolverineThreeStoreCompositionTests.RunMigrationRunnerAsync</c>,
    /// which predates Configuration/Housekeeping and only overrides four of
    /// the six keys (the other two silently fall back to MigrationRunner's
    /// own checked-in appsettings.json default, pointing at the developer's
    /// local Postgres — harmless for that file's own narrower assertions,
    /// but wrong for this test, which needs every schema on the SAME
    /// ephemeral container).
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunMigrationRunnerAsync()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "tools", "IHostPro.MigrationRunner", "bin", "Release", "net10.0", "IHostPro.MigrationRunner.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"MigrationRunner build output not found at {dllPath}. Build IHostPro.MigrationRunner in Release configuration first.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ConnectionStrings__Identity"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = _migratorConnectionString;
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start MigrationRunner process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + await stderrTask;

        return (process.ExitCode, output);
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
