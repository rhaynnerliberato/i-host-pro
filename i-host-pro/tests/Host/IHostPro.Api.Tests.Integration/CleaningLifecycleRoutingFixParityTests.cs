using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.Runtime;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 7, Incremento 1 (Agenda Foundation), Checkpoint 1 closure: proves
/// the real, composed <c>IHostPro.Api</c> routing for the four Cleaning
/// transitions touched by this closure round, against a real broker (never
/// a copy/mirror of the source), mirroring
/// <see cref="CleaningCancelledRoutingParityTests"/>'s own pattern.
///
/// Two of these (<c>cleaning_needs_help</c>, <c>cleaning_needs_material</c>)
/// close a real, previously-undiscovered production defect: both events had
/// real producers since Fase 6 Incremento 2A, staged into Housekeeping's own
/// outbox on every occurrence, but <c>IHostPro.Api</c>'s Wolverine routing
/// table never had a publish rule for either type, so the outbox retried
/// them forever and neither ever reached RabbitMQ. The other two
/// (<c>cleaning_in_transit</c>, <c>cleaning_interrupted</c>) are brand-new
/// Integration Events, approved and added in this same closure round, so
/// that the Agenda projection can materialize the
/// <c>InTransit</c>/<c>Interrupted</c> statuses it previously could not
/// reach. All four are asserted here to route to the exact same
/// <c>housekeeping-events</c> exchange as every other Cleaning event — no
/// new exchange, no divergent topology.
/// </summary>
public sealed class CleaningLifecycleRoutingFixParityTests : IAsyncLifetime
{
    private static readonly (string RoutingKey, string EventNameFragment)[] ExpectedRoutes =
    [
        ("cleaning_needs_help", "cleaning_needs_help"),
        ("cleaning_needs_material", "cleaning_needs_material"),
        ("cleaning_in_transit", "cleaning_in_transit"),
        ("cleaning_interrupted", "cleaning_interrupted"),
    ];

    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_test")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();
        await _postgresContainer.StartAsync();

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
        var migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;

        var (exitCode, output) = await RunMigrationRunnerAsync(migratorConnectionString);
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public void Api_routes_the_four_closure_round_Cleaning_events_to_the_housekeeping_events_exchange_with_the_documented_routing_keys()
    {
        using var signingKey = RSA.Create(2048);
        var values = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Identity__Jwt__Issuer"] = "https://identity.ihostpro.test",
            ["Identity__Jwt__Audience"] = "ihostpro-api-test",
            ["Identity__Jwt__AccessTokenLifetime"] = "00:15:00",
            ["Identity__Jwt__ClockSkew"] = "00:01:00",
            ["Identity__Jwt__SigningKey__PrivateKeyPem"] = signingKey.ExportRSAPrivateKeyPem(),
            ["Identity__AccountLockout__MaxFailedAccessAttempts"] = "5",
            ["Identity__AccountLockout__DefaultLockoutDuration"] = "00:05:00",
            ["Identity__AccountLockout__AllowedForNewUsers"] = "true",
            ["Identity__RefreshToken__Lifetime"] = "30.00:00:00",
            ["Identity__RefreshToken__SecretSizeBytes"] = "32",
            ["Identity__RefreshToken__ConcurrentRotationGraceWindow"] = "00:00:10",
            ["ConnectionStrings__Identity"] = _appConnectionString,
            ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
            ["ConnectionStrings__Reservations"] = _appConnectionString,
            ["ConnectionStrings__Configuration"] = _appConnectionString,
            ["ConnectionStrings__Housekeeping"] = _appConnectionString,
            ["ConnectionStrings__Communication"] = _appConnectionString,
            ["ConnectionStrings__Dashboard"] = _appConnectionString,
            ["ConnectionStrings__Platform"] = _appConnectionString,
            ["Configuration__PolicyCache__ConnectionString"] = "localhost:6379",
            ["RabbitMq__Host"] = _rabbitMqContainer.Hostname,
            ["RabbitMq__VirtualHost"] = "/",
            ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
            ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14324",
        };
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var runtime = (WolverineRuntime)factory.Services.GetRequiredService<IWolverineRuntime>();
            var rabbitEndpoints = runtime.Options.Transports.AllEndpoints()
                .Where(e => e.Uri.Scheme == "rabbitmq")
                .ToArray();

            foreach (var (routingKey, eventNameFragment) in ExpectedRoutes)
            {
                var expectedUri = new Uri($"rabbitmq://exchange/housekeeping-events/routing/{routingKey}");
                var matching = rabbitEndpoints.Where(e => e.Uri.ToString().Contains(eventNameFragment, StringComparison.Ordinal)).ToArray();

                matching.Should().ContainSingle(
                    $"IHostPro.Api must publish a single, unambiguous route for the {eventNameFragment} routing key");
                matching[0].Uri.Should().Be(expectedUri,
                    $"{eventNameFragment} must route to the same housekeeping-events exchange as every other Cleaning event " +
                    "— no new exchange, no divergent topology");
            }
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    private async Task<(int ExitCode, string Output)> RunMigrationRunnerAsync(string migratorConnectionString)
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
        psi.Environment["ConnectionStrings__Identity"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = migratorConnectionString;
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
