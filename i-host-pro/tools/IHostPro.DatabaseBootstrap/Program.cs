using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using IHostPro.DatabaseBootstrap;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;
using System.Text.Json;

// One-off bootstrap tool for a fresh RDS instance: creates/updates the
// ihostpro_migrator / ihostpro_app LOGIN roles idempotently, connecting only
// with the RDS-managed master credential (never used by any other runtime
// component). See DatabaseRoleReconciler.cs for the reconciliation SQL
// itself (extracted so IHostPro.DatabaseBootstrap.Tests.Integration can
// exercise it directly against a real Postgres).
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .Enrich.WithProperty("Application", "IHostPro.DatabaseBootstrap")
    .WriteTo.Console()
    .CreateLogger();

try
{
    var rdsMasterSecretArn = BootstrapConfiguration.RequireConfig(configuration, "DatabaseBootstrap:RdsMasterSecretArn");
    var appSecretArn = BootstrapConfiguration.RequireConfig(configuration, "DatabaseBootstrap:AppSecretArn");
    var migratorSecretArn = BootstrapConfiguration.RequireConfig(configuration, "DatabaseBootstrap:MigratorSecretArn");

    using var secretsClient = new AmazonSecretsManagerClient();

    Log.Information("Reading RDS master credential and target role connection strings from Secrets Manager.");

    var masterSecretJson = await GetSecretStringAsync(secretsClient, rdsMasterSecretArn);
    var master = JsonSerializer.Deserialize<RdsMasterSecret>(masterSecretJson)
        ?? throw new InvalidOperationException("RDS master secret did not deserialize to the expected shape.");

    var appConnectionString = await GetSecretStringAsync(secretsClient, appSecretArn);
    var migratorConnectionString = await GetSecretStringAsync(secretsClient, migratorSecretArn);

    // NpgsqlConnectionStringBuilder, not string splitting/regex - the
    // authoritative, injection-safe way to pull Username/Password back out
    // of a connection string this same codebase generated (RDS module's
    // secret_string_wo).
    var appRole = new NpgsqlConnectionStringBuilder(appConnectionString);
    var migratorRole = new NpgsqlConnectionStringBuilder(migratorConnectionString);

    // CP5.3C corrective Decision Gate item 11: no privileged path may use a
    // weaker TLS mode than the runtime credentials do - the master
    // connection uses the exact same VerifyFull + baked-in RDS CA bundle as
    // database/app and database/migrator (see modules/rds/main.tf).
    var masterConnectionString = new NpgsqlConnectionStringBuilder
    {
        Host = master.Host,
        Port = master.Port,
        Database = master.Dbname,
        Username = master.Username,
        Password = master.Password,
        SslMode = SslMode.VerifyFull,
        RootCertificate = BootstrapConfiguration.RdsCaBundlePath,
    }.ToString();

    await using var connection = new NpgsqlConnection(masterConnectionString);
    await connection.OpenAsync();

    Log.Information("Connected as RDS master credential. Reconciling roles.");

    await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(connection, migratorRole.Username!, migratorRole.Password!);
    await DatabaseRoleReconciler.CreateOrUpdateLoginRoleAsync(connection, appRole.Username!, appRole.Password!);
    await DatabaseRoleReconciler.GrantCreateOnDatabaseAsync(connection, master.Dbname, migratorRole.Username!);

    Log.Information("Database bootstrap completed successfully. Roles {MigratorRole} and {AppRole} are ready.",
        migratorRole.Username, appRole.Username);
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Database bootstrap failed.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<string> GetSecretStringAsync(IAmazonSecretsManager client, string secretArn)
{
    var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretArn });
    return response.SecretString
        ?? throw new InvalidOperationException($"Secret {secretArn} has no SecretString value.");
}
