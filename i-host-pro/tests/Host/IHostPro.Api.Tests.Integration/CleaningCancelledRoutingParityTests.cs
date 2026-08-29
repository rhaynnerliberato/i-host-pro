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
/// Fase 6, Checkpoint 6 homologação, item 11 of the user's approved
/// protocol: <c>CleaningCancelled</c> is the one Housekeeping event
/// published from BOTH <c>IHostPro.Api</c> (HTTP-triggered manual
/// cancellation) and <c>IHostPro.Worker</c> (the automatic
/// ReservationCancelled-driven reaction) — the exact defect found and fixed
/// earlier in this checkpoint was the Worker having no publish rule at all.
///
/// This test anchors <c>IHostPro.Api</c>'s REAL, composed routing (via
/// <see cref="WebApplicationFactory{Program}"/> against a real broker/
/// Postgres, never a copy/mirror of the source) to the exact
/// exchange/routing key Uri. The Worker side of the same guarantee is
/// already proven independently and just as concretely by
/// <c>ReservationCancelledWorkerRoundTripTests</c> and
/// <c>ReservationCancelledRedeliveryTests</c>: both bind a real probe queue
/// to <c>housekeeping-events</c>/<c>cleaning_cancelled</c> BEFORE triggering
/// the real Worker's automatic reaction and confirm real broker delivery
/// onto that exact binding — if the Worker's routing key or exchange ever
/// drifted from this constant, that probe would simply never receive
/// anything and those tests would fail. Together, the two sides are pinned
/// to the identical literal topology — never a shared production
/// abstraction introduced just to remove two duplicated lines, per the
/// user's own explicit instruction not to do that.
/// </summary>
public sealed class CleaningCancelledRoutingParityTests : IAsyncLifetime
{
    private static readonly Uri ExpectedCleaningCancelledUri = new("rabbitmq://exchange/housekeeping-events/routing/cleaning_cancelled");

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
    public void Api_routes_CleaningCancelled_to_the_same_exchange_and_routing_key_the_real_Worker_is_independently_proven_to_use()
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
            ["ConnectionStrings__GuestOperations"] = _appConnectionString,
            ["ConnectionStrings__Payments"] = _appConnectionString,
            ["ConnectionStrings__AIAgent"] = _appConnectionString,
            ["ConnectionStrings__ExternalIntegrations"] = _appConnectionString,
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

            var cleaningCancelledEndpoint = runtime.Options.Transports.AllEndpoints()
                .Single(e => e.Uri.Scheme == "rabbitmq" && e.Uri.ToString().Contains("cleaning_cancelled", StringComparison.Ordinal));

            cleaningCancelledEndpoint.Uri.Should().Be(ExpectedCleaningCancelledUri,
                "IHostPro.Api must publish CleaningCancelled to the exact same exchange/routing key the real Worker is independently proven " +
                "(via ReservationCancelledWorkerRoundTripTests/ReservationCancelledRedeliveryTests) to publish to — no divergent topology");
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
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__GuestOperations"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__Payments"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__AIAgent"] = migratorConnectionString;
        psi.Environment["ConnectionStrings__ExternalIntegrations"] = migratorConnectionString;
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
